using SpinTexture.Core.Models;
using SpinTexture.Core.Textures;
using System.Runtime.ExceptionServices;

namespace SpinTexture.Core.Tooling;

public sealed record NativeTextureProcessRequest(
    string SourcePath,
    string DestinationPath,
    string WorkingDirectory,
    TextureMetadata Metadata,
    TextureClassification Classification,
    UpscaleOptions Options,
    int WrapPadding = 32,
    int RealEsrganTileSize = 0,
    bool WrapEdges = true);

public sealed record NativeTextureProcessResult(
    string DestinationPath,
    UpscaleDimensions Dimensions,
    string ProcessingRoute,
    TimeSpan Elapsed,
    int ExpectedMipCount = 1,
    bool UsedCutoutMipFloor = false);

public sealed record NativeTextureProcessOutcome(
    NativeTextureProcessResult? Result,
    Exception? Error)
{
    public bool Succeeded => Result is not null && Error is null;
}

public sealed class NativeTextureProcessor
{
    private const int MaximumNeuralBatchItems = 64;
    private const long MaximumNeuralBatchOutputPixels = 64L * 1024 * 1024;
    private const string ConservativeNeuralWorkerThreads = "1:2:2";
    private const string AcceleratedSmallTextureWorkerThreads = "1:3:3";

    private readonly ExternalToolPaths _tools;
    private readonly INativeProcessRunner _runner;
    private readonly DirectXTexCommandBuilder _directXTex;
    private readonly RealEsrganCommandBuilder _realEsrgan;
    private readonly UpscaylCommandBuilder _upscayl;
    private int textureUpscalerUnavailable;

    public NativeTextureProcessor(
        ExternalToolPaths tools,
        INativeProcessRunner? runner = null,
        DirectXTexCommandBuilder? directXTex = null,
        RealEsrganCommandBuilder? realEsrgan = null,
        UpscaylCommandBuilder? upscayl = null)
    {
        _tools = tools ?? throw new ArgumentNullException(nameof(tools));
        _runner = runner ?? new NativeProcessRunner();
        _directXTex = directXTex ?? new DirectXTexCommandBuilder();
        _realEsrgan = realEsrgan ?? new RealEsrganCommandBuilder();
        _upscayl = upscayl ?? new UpscaylCommandBuilder();
    }

    public async Task<NativeTextureProcessResult> ProcessAsync(
        NativeTextureProcessRequest request,
        IProgress<NativeOutputLine>? nativeProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcomes = await ProcessBatchAsync(
            [request],
            nativeProgress,
            cancellationToken).ConfigureAwait(false);
        var outcome = outcomes[0];
        if (outcome.Error is not null)
        {
            ExceptionDispatchInfo.Capture(outcome.Error).Throw();
        }

        return outcome.Result
            ?? throw new InvalidOperationException("Native texture processing completed without a result or error.");
    }

    public async Task<IReadOnlyList<NativeTextureProcessOutcome>> ProcessBatchAsync(
        IReadOnlyList<NativeTextureProcessRequest> requests,
        IProgress<NativeOutputLine>? nativeProgress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.Count == 0)
        {
            return [];
        }

        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            ArgumentNullException.ThrowIfNull(request);
            ValidateRequest(request);
            if (!destinations.Add(Path.GetFullPath(request.DestinationPath)))
            {
                throw new ArgumentException(
                    $"A native texture batch cannot write the same destination more than once: {request.DestinationPath}",
                    nameof(requests));
            }
        }

        // Several bundled tools still use legacy Win32 path handling. A single
        // short batch root also lets the Vulkan worker retain its loaded model
        // while it processes a directory of compatible inputs.
        var operationRoot = Path.Combine(Path.GetTempPath(), "SpinTexture");
        Directory.CreateDirectory(operationRoot);
        var batchDirectory = Path.Combine(operationRoot, $"texture-batch-{Guid.NewGuid():N}");
        Directory.CreateDirectory(batchDirectory);
        var outcomes = new NativeTextureProcessOutcome?[requests.Count];
        var pendingColorJobs = new List<PendingColorJob>();
        long pendingColorOutputPixels = 0;

        try
        {
            for (var index = 0; index < requests.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var request = requests[index];
                var dimensions = UpscaleDimensions.Calculate(
                    request.Metadata.Width,
                    request.Metadata.Height,
                    request.Options.MaximumDimension);
                var started = DateTimeOffset.UtcNow;

                if (!dimensions.RequiresUpscale)
                {
                    try
                    {
                        await CopyFileAsync(
                            request.SourcePath,
                            request.DestinationPath,
                            cancellationToken).ConfigureAwait(false);
                        outcomes[index] = new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                dimensions,
                                "Unchanged: maximum dimension already reached",
                                DateTimeOffset.UtcNow - started,
                                Math.Max(1, request.Metadata.MipCount)),
                            Error: null);
                    }
                    catch (Exception exception) when (IsPerTextureFailure(exception))
                    {
                        outcomes[index] = new NativeTextureProcessOutcome(Result: null, Error: exception);
                    }

                    continue;
                }

                var jobDirectory = Path.Combine(batchDirectory, $"job-{index:D6}");
                Directory.CreateDirectory(jobDirectory);
                var localSourcePath = Path.Combine(
                    jobDirectory,
                    $"source{Path.GetExtension(request.SourcePath)}");

                try
                {
                    if (request.Classification.CanUseColorUpscaler)
                    {
                        var estimatedOutputPixels = EstimateNeuralOutputPixels(
                            request,
                            dimensions);
                        if (pendingColorJobs.Count > 0
                            && (pendingColorJobs.Count >= MaximumNeuralBatchItems
                                || pendingColorOutputPixels + estimatedOutputPixels
                                    > MaximumNeuralBatchOutputPixels))
                        {
                            var readyBatch = pendingColorJobs.ToArray();
                            pendingColorJobs.Clear();
                            pendingColorOutputPixels = 0;
                            await PrepareAndProcessColorJobsAsync(
                                readyBatch,
                                outcomes,
                                batchDirectory,
                                nativeProgress,
                                cancellationToken).ConfigureAwait(false);
                        }

                        pendingColorJobs.Add(new PendingColorJob(
                            index,
                            request,
                            dimensions,
                            jobDirectory,
                            localSourcePath,
                            started));
                        pendingColorOutputPixels = checked(
                            pendingColorOutputPixels + estimatedOutputPixels);
                    }
                    else
                    {
                        await CopyTemporaryFileAsync(
                            request.SourcePath,
                            localSourcePath,
                            cancellationToken).ConfigureAwait(false);
                        var nativeRequest = request with { SourcePath = localSourcePath };
                        var mipResult = await ProcessDataTextureAsync(
                            nativeRequest,
                            dimensions,
                            jobDirectory,
                            nativeProgress,
                            cancellationToken).ConfigureAwait(false);
                        outcomes[index] = new NativeTextureProcessOutcome(
                            new NativeTextureProcessResult(
                                request.DestinationPath,
                                dimensions,
                                "DirectXTex data-safe cubic resize",
                                DateTimeOffset.UtcNow - started,
                                mipResult.MipCount,
                                mipResult.UsedCutoutFloor),
                            Error: null);
                        TryDeleteOwnedChildDirectory(batchDirectory, jobDirectory, "job-");
                    }
                }
                catch (Exception exception) when (IsPerTextureFailure(exception))
                {
                    outcomes[index] = new NativeTextureProcessOutcome(Result: null, Error: exception);
                    TryDeleteOwnedChildDirectory(batchDirectory, jobDirectory, "job-");
                }
            }

            if (pendingColorJobs.Count > 0)
            {
                await PrepareAndProcessColorJobsAsync(
                    pendingColorJobs,
                    outcomes,
                    batchDirectory,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }

            if (outcomes.Any(outcome => outcome is null))
            {
                throw new InvalidOperationException("A native texture batch left one or more requests unresolved.");
            }

            return outcomes.Select(outcome => outcome!).ToArray();
        }
        finally
        {
            TryDeleteOperationDirectory(operationRoot, batchDirectory);
        }
    }

    internal async Task<bool> DetectAlphaTestedCutoutAsync(
        string sourcePath,
        TextureMetadata metadata,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentNullException.ThrowIfNull(metadata);
        if (!metadata.HasAlpha)
        {
            return false;
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Source texture was not found.", sourcePath);
        }

        EnsureDirectXTex();
        var operationRoot = Path.Combine(Path.GetTempPath(), "SpinTexture");
        Directory.CreateDirectory(operationRoot);
        var operationDirectory = Path.Combine(
            operationRoot,
            $"texture-alpha-{Guid.NewGuid():N}");
        Directory.CreateDirectory(operationDirectory);

        try
        {
            var localSourcePath = Path.Combine(
                operationDirectory,
                $"source{Path.GetExtension(sourcePath)}");
            await CopyTemporaryFileAsync(
                sourcePath,
                localSourcePath,
                cancellationToken).ConfigureAwait(false);
            var decodedDirectory = CreateDirectory(operationDirectory, "decoded");
            await RunCheckedAsync(
                _directXTex.CreateConvert(
                    _tools.TexconvPath!,
                    localSourcePath,
                    decodedDirectory,
                    "tga"),
                nativeProgress: null,
                cancellationToken).ConfigureAwait(false);
            var decodedPath = FindSingleOutput(decodedDirectory, ".tga");
            var decoded = await TgaPixelBuffer
                .ReadFileAsync(decodedPath, cancellationToken)
                .ConfigureAwait(false);
            if (decoded.Width != metadata.Width || decoded.Height != metadata.Height)
            {
                throw new InvalidDataException(
                    "DirectXTex alpha inspection dimensions do not match the source header.");
            }

            return decoded.HasLikelyCutoutAlpha();
        }
        finally
        {
            TryDeleteOperationDirectory(operationRoot, operationDirectory);
        }
    }

    private async Task PrepareAndProcessColorJobsAsync(
        IReadOnlyList<PendingColorJob> pendingJobs,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        if (pendingJobs.Count == 0)
        {
            return;
        }

        var prepared = new PreparedColorJob?[pendingJobs.Count];
        var preparedCount = 0;
        using var gate = new SemaphoreSlim(
            Math.Min(
                pendingJobs.Count,
                SelectCpuPreparationParallelism(Environment.ProcessorCount)));
        var preparationTasks = pendingJobs.Select(async (pending, position) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                nativeProgress?.Report(new NativeOutputLine(
                    NativeOutputStream.StandardOutput,
                    $"Preparing textures for a {pendingJobs.Count:N0}-item neural batch.",
                    NativeTextureWorkPhase.CpuPreparation,
                    Volatile.Read(ref preparedCount),
                    pendingJobs.Count));
                await CopyTemporaryFileAsync(
                    pending.Request.SourcePath,
                    pending.LocalSourcePath,
                    cancellationToken).ConfigureAwait(false);
                prepared[position] = await PrepareColorJobAsync(
                    pending.RequestIndex,
                    pending.Request with { SourcePath = pending.LocalSourcePath },
                    pending.Dimensions,
                    pending.OperationDirectory,
                    pending.Started,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
                var completedPreparation = Interlocked.Increment(ref preparedCount);
                nativeProgress?.Report(new NativeOutputLine(
                    NativeOutputStream.StandardOutput,
                    $"Prepared texture {completedPreparation:N0} of {pendingJobs.Count:N0} for the neural batch.",
                    NativeTextureWorkPhase.CpuPreparation,
                    completedPreparation,
                    pendingJobs.Count));
            }
            catch (Exception exception) when (IsPerTextureFailure(exception))
            {
                outcomes[pending.RequestIndex] = new NativeTextureProcessOutcome(
                    Result: null,
                    Error: exception);
                TryDeleteOwnedChildDirectory(
                    batchDirectory,
                    pending.OperationDirectory,
                    "job-");
            }
            catch
            {
                TryDeleteOwnedChildDirectory(
                    batchDirectory,
                    pending.OperationDirectory,
                    "job-");
                throw;
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();

        try
        {
            await Task.WhenAll(preparationTasks).ConfigureAwait(false);
            var ready = prepared
                .Where(job => job is not null)
                .Select(job => job!)
                .OrderBy(job => job.RequestIndex)
                .ToArray();
            if (ready.Length > 0)
            {
                await ProcessPreparedColorJobsAsync(
                    ready,
                    outcomes,
                    batchDirectory,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            CleanupPreparedJobDirectories(
                batchDirectory,
                prepared.Where(job => job is not null).Select(job => job!));
        }
    }

    internal static int SelectCpuPreparationParallelism(int processorCount)
    {
        if (processorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        }

        // Each lane launches a separate texconv decoder. Leaving most logical
        // processors free prevents native image codecs, the UI, and disk I/O
        // from fighting one another on slower systems.
        return Math.Clamp((processorCount + 3) / 4, 1, 4);
    }

    private async Task<PreparedColorJob> PrepareColorJobAsync(
        int requestIndex,
        NativeTextureProcessRequest request,
        UpscaleDimensions dimensions,
        string operationDirectory,
        DateTimeOffset started,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        EnsureColorTools();
        var decodedDirectory = CreateDirectory(operationDirectory, "decoded");
        await RunCheckedAsync(
            _directXTex.CreateConvert(_tools.TexconvPath!, request.SourcePath, decodedDirectory, "tga"),
            nativeProgress,
            cancellationToken).ConfigureAwait(false);

        var decodedPath = FindSingleOutput(decodedDirectory, ".tga");
        var decoded = await TgaPixelBuffer.ReadFileAsync(decodedPath, cancellationToken).ConfigureAwait(false);
        if (decoded.Width != request.Metadata.Width || decoded.Height != request.Metadata.Height)
        {
            throw new InvalidDataException("DirectXTex decoded dimensions do not match the source header.");
        }

        if (request.Metadata.HasAlpha && decoded.IsFullyTransparent())
        {
            throw new NotSupportedException(
                "Entirely transparent control textures are preserved because reconstructing them can expose an engine error material.");
        }

        var preserveAlphaCoverage = decoded.HasLikelyCutoutAlpha();
        var hasSoftTranslucentAlpha = request.Metadata.HasAlpha
            && decoded.HasSoftTranslucentAlpha();
        var colorSource = preserveAlphaCoverage
            ? decoded.DilateRgbIntoTransparentPixels()
            : decoded;
        var opaque = colorSource.WithOpaqueAlpha();
        var bordered = request.WrapEdges
            ? opaque.AddWrappedBorder(request.WrapPadding)
            : opaque.AddClampedBorder(request.WrapPadding);
        var neuralInputPath = Path.Combine(operationDirectory, "neural-input.png");
        await bordered.WritePngFileAsync(neuralInputPath, cancellationToken).ConfigureAwait(false);

        return new PreparedColorJob(
            requestIndex,
            request,
            dimensions,
            operationDirectory,
            neuralInputPath,
            decoded,
            preserveAlphaCoverage,
            hasSoftTranslucentAlpha
                ? TexturePreset.Faithful
                : request.Options.Preset,
            hasSoftTranslucentAlpha,
            started);
    }

    private async Task ProcessPreparedColorJobsAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var specializedModel = Volatile.Read(ref textureUpscalerUnavailable) == 0
                               && _tools.HasTextureUpscaler
            ? _upscayl.FindTextureModel(_tools.TextureModelsPath!)
            : null;
        var executionState = new NeuralBatchExecutionState();
        var groups = jobs
            .OrderBy(job => job.RequestIndex)
            .GroupBy(job => CreateNeuralBatchKey(job, specializedModel));

        foreach (var group in groups)
        {
            foreach (var chunk in ChunkNeuralJobs(group))
            {
                await ExecuteNeuralGroupAsync(
                    chunk,
                    group.Key,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private NeuralBatchKey CreateNeuralBatchKey(
        PreparedColorJob job,
        TextureUpscaleModelSelection? specializedModel)
    {
        if (ShouldUseTextureSpecializedReconstruction(job.EffectivePreset)
            && Volatile.Read(ref textureUpscalerUnavailable) == 0
            && specializedModel is not null)
        {
            return new NeuralBatchKey(
                NeuralWorkerKind.TextureSpecialized,
                specializedModel.Name,
                job.EffectivePreset,
                UpscaylCommandBuilder.ModelScale,
                job.Request.RealEsrganTileSize);
        }

        // Classic HD is defined by the texture-specialized worker. If it is
        // unavailable, a restrained ESRNet reconstruction is safer and more
        // faithful than silently substituting the generic detail GAN.
        var workerPreset = job.EffectivePreset == TexturePreset.ClassicHd
            ? TexturePreset.Faithful
            : job.EffectivePreset;
        var model = _realEsrgan.ResolveModel(_tools.RealEsrganModelsPath!, workerPreset);
        return new NeuralBatchKey(
            NeuralWorkerKind.LegacyRealEsrgan,
            model.Name,
            workerPreset,
            job.Dimensions.RequiredNeuralScale,
            job.Request.RealEsrganTileSize);
    }

    private static bool ShouldUseTextureSpecializedReconstruction(TexturePreset preset) =>
        preset is TexturePreset.ClassicHd
            or TexturePreset.Illustrated;

    private static bool IsPaintedPreset(TexturePreset preset) =>
        preset is TexturePreset.Illustrated or TexturePreset.RusticPainted;

    private NeuralBatchKey CreateLegacyPaintedFallbackKey(PreparedColorJob job)
    {
        if (!IsPaintedPreset(job.EffectivePreset))
        {
            throw new InvalidOperationException(
                "Only a painted texture can enter the legacy painted reconstruction fallback.");
        }

        var model = _realEsrgan.ResolveModel(
            _tools.RealEsrganModelsPath!,
            job.EffectivePreset);
        return new NeuralBatchKey(
            NeuralWorkerKind.LegacyRealEsrgan,
            model.Name,
            job.EffectivePreset,
            job.Dimensions.RequiredNeuralScale,
            job.Request.RealEsrganTileSize);
    }

    private NeuralBatchKey CreateFaithfulFallbackKey(PreparedColorJob job)
    {
        var model = _realEsrgan.ResolveModel(
            _tools.RealEsrganModelsPath!,
            TexturePreset.Faithful);
        return new NeuralBatchKey(
            NeuralWorkerKind.LegacyRealEsrgan,
            model.Name,
            TexturePreset.Faithful,
            job.Dimensions.RequiredNeuralScale,
            job.Request.RealEsrganTileSize);
    }

    private async Task ExecuteNeuralGroupAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NeuralBatchKey key,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        NeuralBatchExecutionState executionState,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (jobs.Count == 0)
        {
            return;
        }

        if (key.WorkerKind == NeuralWorkerKind.TextureSpecialized
            && Volatile.Read(ref textureUpscalerUnavailable) != 0)
        {
            await ExecuteTextureSpecializedFallbackGroupsAsync(
                    jobs,
                    key.WorkerPreset,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        NeuralBatchInvocation invocation;
        var workerThreads = SelectNeuralWorkerThreads(
            Environment.ProcessorCount,
            jobs.Count,
            jobs.Max(job => EstimateNeuralOutputPixels(job.Request, job.Dimensions)));
        try
        {
            try
            {
                invocation = await InvokeNeuralBatchAsync(
                    jobs,
                    key,
                    batchDirectory,
                    executionState,
                    workerThreads,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NativeProcessException) when (
                !workerThreads.Equals(
                    ConservativeNeuralWorkerThreads,
                    StringComparison.Ordinal))
            {
                nativeProgress?.Report(new NativeOutputLine(
                    NativeOutputStream.StandardError,
                    "The accelerated small-texture queue failed; retrying the same model with the conservative Vulkan queue."));
                invocation = await InvokeNeuralBatchAsync(
                    jobs,
                    key,
                    batchDirectory,
                    executionState,
                    ConservativeNeuralWorkerThreads,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            catch (NativeProcessInactivityException)
            {
                nativeProgress?.Report(new NativeOutputLine(
                    NativeOutputStream.StandardError,
                    "The GPU worker stopped reporting activity after a possible driver reset; retrying the same model once without changing quality.",
                    NativeTextureWorkPhase.GpuInference));
                invocation = await InvokeNeuralBatchAsync(
                    jobs,
                    key,
                    batchDirectory,
                    executionState,
                    ConservativeNeuralWorkerThreads,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
        catch (NativeProcessException) when (key.WorkerKind == NeuralWorkerKind.TextureSpecialized)
        {
            Interlocked.Exchange(ref textureUpscalerUnavailable, 1);
            nativeProgress?.Report(new NativeOutputLine(
                NativeOutputStream.StandardError,
                IsPaintedPreset(key.WorkerPreset)
                    ? "The game-texture batch worker could not run; retrying this painted batch with the bundled illustrated model."
                    : "The game-texture batch worker could not run; retrying this batch with the bundled faithful ESRNet model."));
            await ExecuteTextureSpecializedFallbackGroupsAsync(
                    jobs,
                    key.WorkerPreset,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }
        catch (NativeProcessException) when (key.WorkerPreset == TexturePreset.MaximumDetail)
        {
            nativeProgress?.Report(new NativeOutputLine(
                NativeOutputStream.StandardError,
                "The maximum-detail batch worker failed; retrying this batch with the faithful ESRNet model."));
            await ExecuteFallbackGroupsAsync(
                jobs,
                outcomes,
                batchDirectory,
                executionState,
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
            return;
        }
        catch (Exception exception) when (
            IsPerTextureFailure(exception)
            && key.WorkerPreset != TexturePreset.Faithful)
        {
            nativeProgress?.Report(new NativeOutputLine(
                NativeOutputStream.StandardError,
                key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                && IsPaintedPreset(key.WorkerPreset)
                    ? $"The texture-specialized {key.WorkerPreset} batch returned unusable output; retrying with the bundled illustrated model. {exception.Message}"
                    : $"The {key.WorkerPreset} batch returned unusable output; retrying this batch with the faithful ESRNet model. {exception.Message}"));
            if (key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                && IsPaintedPreset(key.WorkerPreset))
            {
                await ExecuteLegacyPaintedFallbackGroupsAsync(
                        jobs,
                        outcomes,
                        batchDirectory,
                        executionState,
                        nativeProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await ExecuteFallbackGroupsAsync(
                        jobs,
                        outcomes,
                        batchDirectory,
                        executionState,
                        nativeProgress,
                        cancellationToken)
                    .ConfigureAwait(false);
            }

            return;
        }
        catch (Exception exception) when (IsPerTextureFailure(exception))
        {
            if (jobs.Count > 1)
            {
                var middle = jobs.Count / 2;
                await ExecuteNeuralGroupAsync(
                    jobs.Take(middle).ToArray(),
                    key,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
                await ExecuteNeuralGroupAsync(
                    jobs.Skip(middle).ToArray(),
                    key,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
            else
            {
                outcomes[jobs[0].RequestIndex] = new NativeTextureProcessOutcome(Result: null, Error: exception);
            }

            return;
        }

        var legacyPaintedRetries = new List<PreparedColorJob>();
        var faithfulRetries = new List<PreparedColorJob>();
        try
        {
            var completions = await CompleteColorJobsInParallelAsync(
                jobs,
                key,
                invocation,
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < jobs.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var job = jobs[index];
                var completion = completions[index];
                if (completion.Error is null)
                {
                    outcomes[job.RequestIndex] = new NativeTextureProcessOutcome(
                        completion.Result
                            ?? throw new InvalidDataException(
                                "Parallel texture completion returned neither output nor an error."),
                        Error: null);
                }
                else if (key.WorkerPreset != TexturePreset.Faithful)
                {
                    if (key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                        && IsPaintedPreset(key.WorkerPreset))
                    {
                        nativeProgress?.Report(new NativeOutputLine(
                            NativeOutputStream.StandardError,
                            $"{Path.GetFileName(job.Request.SourcePath)} failed the texture-specialized {key.WorkerPreset} fidelity gate; queued for the bundled illustrated fallback batch. {completion.Error.Message}"));
                        legacyPaintedRetries.Add(job);
                    }
                    else
                    {
                        nativeProgress?.Report(new NativeOutputLine(
                            NativeOutputStream.StandardError,
                            $"{Path.GetFileName(job.Request.SourcePath)} failed the {key.WorkerPreset} fidelity gate; queued for the faithful ESRNet fallback batch. {completion.Error.Message}"));
                        faithfulRetries.Add(job);
                    }
                }
                else
                {
                    outcomes[job.RequestIndex] = new NativeTextureProcessOutcome(
                        Result: null,
                        Error: completion.Error);
                }
            }
        }
        finally
        {
            TryDeleteOwnedChildDirectory(batchDirectory, invocation.GroupDirectory, "neural-");
        }

        if (legacyPaintedRetries.Count > 0)
        {
            await ExecuteLegacyPaintedFallbackGroupsAsync(
                legacyPaintedRetries,
                outcomes,
                batchDirectory,
                executionState,
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
        }

        if (faithfulRetries.Count > 0)
        {
            await ExecuteFallbackGroupsAsync(
                faithfulRetries,
                outcomes,
                batchDirectory,
                executionState,
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<ColorJobCompletion>> CompleteColorJobsInParallelAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NeuralBatchKey key,
        NeuralBatchInvocation invocation,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var maximumOutputPixels = jobs.Max(
            job => EstimateNeuralOutputPixels(job.Request, job.Dimensions));
        var availableMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        if (availableMemory <= 0)
        {
            availableMemory = 2L * 1024 * 1024 * 1024;
        }

        var parallelism = SelectCpuPostprocessParallelism(
            Environment.ProcessorCount,
            availableMemory,
            maximumOutputPixels);
        var completions = new ColorJobCompletion?[jobs.Count];
        using var gate = new SemaphoreSlim(Math.Min(jobs.Count, parallelism));
        var tasks = jobs.Select(async (job, index) =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                completions[index] = new ColorJobCompletion(
                    await CompleteColorJobAsync(
                        job,
                        key,
                        invocation.OutputPaths[job.RequestIndex],
                        nativeProgress,
                        cancellationToken).ConfigureAwait(false),
                    Error: null);
            }
            catch (Exception exception) when (IsPerTextureFailure(exception))
            {
                completions[index] = new ColorJobCompletion(Result: null, Error: exception);
            }
            finally
            {
                gate.Release();
            }
        }).ToArray();
        await Task.WhenAll(tasks).ConfigureAwait(false);
        return completions
            .Select(completion => completion
                ?? throw new InvalidOperationException(
                    "Parallel texture completion left a job unresolved."))
            .ToArray();
    }

    internal static int SelectCpuPostprocessParallelism(
        int processorCount,
        long availableMemoryBytes,
        long maximumOutputPixels)
    {
        if (processorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        }

        if (availableMemoryBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(availableMemoryBytes));
        }

        if (maximumOutputPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputPixels));
        }

        var cpuLanes = Math.Clamp((processorCount + 3) / 4, 1, 4);
        var memoryBudget = Math.Clamp(
            availableMemoryBytes / 12,
            128L * 1024 * 1024,
            768L * 1024 * 1024);
        var estimatedBytesPerLane = checked(
            (64L * 1024 * 1024) + (maximumOutputPixels * 20));
        var memoryLanes = Math.Max(1, memoryBudget / estimatedBytesPerLane);
        return checked((int)Math.Min(cpuLanes, memoryLanes));
    }

    private async Task ExecuteFallbackGroupsAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        NeuralBatchExecutionState executionState,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        foreach (var group in jobs
                     .OrderBy(job => job.RequestIndex)
                     .GroupBy(CreateFaithfulFallbackKey))
        {
            foreach (var chunk in ChunkNeuralJobs(group))
            {
                await ExecuteNeuralGroupAsync(
                    chunk,
                    group.Key,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteTextureSpecializedFallbackGroupsAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        TexturePreset requestedPreset,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        NeuralBatchExecutionState executionState,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        if (IsPaintedPreset(requestedPreset))
        {
            await ExecuteLegacyPaintedFallbackGroupsAsync(
                    jobs,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        await ExecuteFallbackGroupsAsync(
                jobs,
                outcomes,
                batchDirectory,
                executionState,
                nativeProgress,
                cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task ExecuteLegacyPaintedFallbackGroupsAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NativeTextureProcessOutcome?[] outcomes,
        string batchDirectory,
        NeuralBatchExecutionState executionState,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        foreach (var group in jobs
                     .OrderBy(job => job.RequestIndex)
                     .GroupBy(CreateLegacyPaintedFallbackKey))
        {
            foreach (var chunk in ChunkNeuralJobs(group))
            {
                await ExecuteNeuralGroupAsync(
                    chunk,
                    group.Key,
                    outcomes,
                    batchDirectory,
                    executionState,
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<NeuralBatchInvocation> InvokeNeuralBatchAsync(
        IReadOnlyList<PreparedColorJob> jobs,
        NeuralBatchKey key,
        string batchDirectory,
        NeuralBatchExecutionState executionState,
        string workerThreads,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var groupDirectory = Path.Combine(
            batchDirectory,
            $"neural-{executionState.NextGroupId++:D6}");
        var inputDirectory = Path.Combine(groupDirectory, "input");
        var outputDirectory = Path.Combine(groupDirectory, "output");
        Directory.CreateDirectory(inputDirectory);
        Directory.CreateDirectory(outputDirectory);
        var expectedNames = new Dictionary<string, PreparedColorJob>(StringComparer.OrdinalIgnoreCase);

        try
        {
            foreach (var job in jobs.OrderBy(candidate => candidate.RequestIndex))
            {
                var fileName = $"j{job.RequestIndex:D8}.png";
                expectedNames.Add(fileName, job);
                await CopyTemporaryFileAsync(
                    job.NeuralInputPath,
                    Path.Combine(inputDirectory, fileName),
                    cancellationToken).ConfigureAwait(false);
            }

            var command = key.WorkerKind switch
            {
                NeuralWorkerKind.TextureSpecialized => _upscayl.CreateUpscale(
                    _tools.TextureUpscalerPath!,
                    _tools.TextureModelsPath!,
                    inputDirectory,
                    outputDirectory,
                    key.ModelName,
                    key.TileSize,
                    workerThreads),
                NeuralWorkerKind.LegacyRealEsrgan => _realEsrgan.CreateUpscale(
                    _tools.RealEsrganPath!,
                    _tools.RealEsrganModelsPath!,
                    inputDirectory,
                    outputDirectory,
                    key.WorkerPreset,
                    key.TileSize,
                    key.NeuralScale,
                    key.ModelName,
                    workerThreads),
                _ => throw new ArgumentOutOfRangeException(nameof(key))
            };
            var materialDetail = key.WorkerPreset == TexturePreset.MaximumDetail;
            nativeProgress?.Report(new NativeOutputLine(
                NativeOutputStream.StandardOutput,
                materialDetail
                    ? $"GPU Material Detail with eight-view TTA: processing {jobs.Count:N0} texture(s)."
                    : $"GPU enhancement: processing {jobs.Count:N0} texture(s) with {key.ModelName}.",
                NativeTextureWorkPhase.GpuInference,
                0,
                jobs.Count));
            await RunCheckedAsync(command, nativeProgress, cancellationToken).ConfigureAwait(false);
            nativeProgress?.Report(new NativeOutputLine(
                NativeOutputStream.StandardOutput,
                $"GPU batch complete; encoding and validating {jobs.Count:N0} texture(s).",
                NativeTextureWorkPhase.Encoding,
                0,
                jobs.Count));

            var actualNames = Directory.EnumerateFiles(outputDirectory, "*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null)
                .Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!actualNames.SetEquals(expectedNames.Keys))
            {
                var missing = expectedNames.Keys.Except(actualNames, StringComparer.OrdinalIgnoreCase);
                var unexpected = actualNames.Except(expectedNames.Keys, StringComparer.OrdinalIgnoreCase);
                throw new InvalidDataException(
                    "The neural batch output set did not match its inputs. "
                    + $"Missing: {string.Join(", ", missing)}. "
                    + $"Unexpected: {string.Join(", ", unexpected)}.");
            }

            return new NeuralBatchInvocation(
                groupDirectory,
                expectedNames.ToDictionary(
                    pair => pair.Value.RequestIndex,
                    pair => Path.Combine(outputDirectory, pair.Key)));
        }
        catch
        {
            TryDeleteOwnedChildDirectory(batchDirectory, groupDirectory, "neural-");
            throw;
        }
    }

    private async Task<NativeTextureProcessResult> CompleteColorJobAsync(
        PreparedColorJob job,
        NeuralBatchKey key,
        string neuralOutputPath,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var neural = await TgaPixelBuffer.ReadPngFileAsync(
            neuralOutputPath,
            cancellationToken).ConfigureAwait(false);
        var expectedBorderedWidth = checked(
            (job.Request.Metadata.Width + (job.Request.WrapPadding * 2)) * key.NeuralScale);
        var expectedBorderedHeight = checked(
            (job.Request.Metadata.Height + (job.Request.WrapPadding * 2)) * key.NeuralScale);
        if (neural.Width != expectedBorderedWidth || neural.Height != expectedBorderedHeight)
        {
            throw new InvalidDataException(
                $"Neural {key.NeuralScale}x output dimensions were {neural.Width}x{neural.Height}; "
                + $"expected bordered dimensions {expectedBorderedWidth}x{expectedBorderedHeight}.");
        }

        var scaledPadding = checked(job.Request.WrapPadding * key.NeuralScale);
        var expectedWidth = checked(job.Request.Metadata.Width * key.NeuralScale);
        var expectedHeight = checked(job.Request.Metadata.Height * key.NeuralScale);
        var cropped = neural.Crop(scaledPadding, scaledPadding, expectedWidth, expectedHeight);
        if (job.Request.Metadata.HasAlpha)
        {
            cropped = cropped.WithScaledAlphaFrom(
                job.Decoded,
                preserveCoverage: job.PreserveAlphaCoverage,
                wrapEdges: job.Request.WrapEdges);
        }

        var fidelity = CalculateFidelityMetrics(job.Decoded, cropped);
        if (key.WorkerPreset is not (TexturePreset.Illustrated or TexturePreset.RusticPainted)
            || (key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                && IsPaintedPreset(key.WorkerPreset)))
        {
            var anchored = cropped.AnchorVisibleChannelMeansFrom(job.Decoded);
            var anchoredFidelity = CalculateFidelityMetrics(job.Decoded, anchored.Image);
            var anchorReducesError =
                anchoredFidelity.RoundTripMeanSquaredError < fidelity.RoundTripMeanSquaredError
                && anchoredFidelity.MeanLuminanceDrift <= fidelity.MeanLuminanceDrift
                && anchoredFidelity.MaximumMeanChannelDrift <= fidelity.MaximumMeanChannelDrift;
            var anchorRepairsDrift =
                (fidelity.MeanLuminanceDrift > 6 || fidelity.MaximumMeanChannelDrift > 12)
                && anchoredFidelity.MeanLuminanceDrift <= 6
                && anchoredFidelity.MaximumMeanChannelDrift <= 12
                && anchoredFidelity.RoundTripLuminancePsnr >= 27.5;
            if (anchorReducesError || anchorRepairsDrift)
            {
                cropped = anchored.Image;
                fidelity = anchoredFidelity;
            }
        }

        ValidateNeuralOutputForPreset(key.WorkerPreset, fidelity);
        double? graphicPaintedScale = null;
        double? paintedThemeScale = null;
        var illustratedFaithfulFallback = key.WorkerPreset == TexturePreset.Faithful
            && job.EffectivePreset == TexturePreset.Illustrated;
        GeneratedMipResult? mipResult = null;
        if (key.WorkerPreset == TexturePreset.Illustrated || illustratedFaithfulFallback)
        {
            var reconstructionStrengthScale = illustratedFaithfulFallback ? 0.62 : 1d;
            var themeStrengthScale = illustratedFaithfulFallback ? 0.70 : 1d;
            var reconstruction = cropped;
            var requiresFinalValidation = RequiresFinalIllustratedDecodeValidation(
                job.Request.Metadata,
                job.Request.Classification,
                job.PreserveAlphaCoverage);
            var encodedCandidatePath = requiresFinalValidation
                ? Path.Combine(
                    job.OperationDirectory,
                    $"illustrated-encoded-candidate{Path.GetExtension(job.Request.DestinationPath)}")
                : job.Request.DestinationPath;
            InvalidDataException? lastPaintedFailure = null;
            // The stylization passes are strength-independent, so run them
            // once and probe the descending-strength ladder with cheap blends.
            var stylized = reconstruction.ApplyPaintedStylizationFull(
                job.Request.WrapEdges,
                key.NeuralScale,
                job.Request.Options.ResolvedPaintedStyle);
            double[] finishScales = [1d, 0.75d, 0.5d, 0.25d];
            foreach (var finishScale in finishScales)
            {
                try
                {
                    var graphicPainted = reconstruction.BlendTowardStylized(
                        stylized,
                        GetGraphicPaintedStrength(job.Request, job.PreserveAlphaCoverage)
                            * reconstructionStrengthScale
                            * finishScale);
                    ValidateGraphicPaintedOutput(
                        CalculateFidelityMetrics(job.Decoded, graphicPainted));
                    var themed = ApplyValidatedPaintedTheme(
                        job.Decoded,
                        graphicPainted,
                        job.Request.Options.PaintedTheme,
                        GetPaintedThemeStrength(job.Request, job.PreserveAlphaCoverage)
                            * themeStrengthScale);
                    cropped = themed.Image;
                    var candidateMipResult = await EncodeCurrentImageAsync(
                            cropped,
                            encodedCandidatePath)
                        .ConfigureAwait(false);
                    if (requiresFinalValidation)
                    {
                        await ValidateFinalIllustratedOutputAsync(
                                job.Request with { DestinationPath = encodedCandidatePath },
                                job.Decoded,
                                job.OperationDirectory,
                                nativeProgress,
                                cancellationToken)
                            .ConfigureAwait(false);
                        await CopyFileAsync(
                                encodedCandidatePath,
                                job.Request.DestinationPath,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    // Do not publish an encoded candidate until every required
                    // post-encode fidelity check has accepted those exact bytes.
                    mipResult = candidateMipResult;
                    graphicPaintedScale = reconstructionStrengthScale * finishScale;
                    paintedThemeScale = themed.StrengthScale * themeStrengthScale;
                    break;
                }
                catch (InvalidDataException exception)
                {
                    lastPaintedFailure = exception;
                }
            }

            if (mipResult is null)
            {
                throw new InvalidDataException(
                    "The encoded graphic-painted treatment exceeded its bounded fidelity limits even at reduced detail-preserving strength.",
                    lastPaintedFailure);
            }
        }
        else if (key.WorkerPreset == TexturePreset.RusticPainted)
        {
            var painted = cropped.ApplyRusticPaintedGrade(
                GetRusticPaintedStrength(job.Request));
            ValidateStylizedOutput(CalculateFidelityMetrics(job.Decoded, painted));
            cropped = painted;
            mipResult = await EncodeCurrentImageAsync(cropped).ConfigureAwait(false);
        }
        else
        {
            mipResult = await EncodeCurrentImageAsync(cropped).ConfigureAwait(false);
        }

        return new NativeTextureProcessResult(
            job.Request.DestinationPath,
            job.Dimensions,
            GetProcessingRoute(job, key, graphicPaintedScale, paintedThemeScale),
            DateTimeOffset.UtcNow - job.Started,
            mipResult.MipCount,
            mipResult.UsedCutoutFloor);

        async Task<GeneratedMipResult> EncodeCurrentImageAsync(
            TgaPixelBuffer image,
            string? destinationPath = null)
        {
            var mergedPath = Path.Combine(job.OperationDirectory, "merged.tga");
            await image.WriteFileAsync(mergedPath, cancellationToken).ConfigureAwait(false);
            return await EncodeToDestinationAsync(
                destinationPath is null
                    ? job.Request
                    : job.Request with { DestinationPath = destinationPath },
                job.Dimensions,
                mergedPath,
                job.OperationDirectory,
                nativeProgress,
                job.PreserveAlphaCoverage,
                cancellationToken,
                resizeFilter: expectedWidth > job.Dimensions.OutputWidth
                              || expectedHeight > job.Dimensions.OutputHeight
                    ? "FANT"
                    : "CUBIC").ConfigureAwait(false);
        }
    }

    private static string GetProcessingRoute(
        PreparedColorJob job,
        NeuralBatchKey key,
        double? graphicPaintedScale,
        double? paintedThemeScale)
    {
        if (job.HasSoftTranslucentAlpha)
        {
            return "Real-ESRNet soft-alpha restoration with original translucency";
        }

        if (job.EffectivePreset == TexturePreset.ClassicHd)
        {
            return key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                ? $"Batched game-texture reconstruction ({key.ModelName})"
                : "Batched faithful Real-ESRNet reconstruction (game-texture fallback)";
        }

        return key.WorkerPreset switch
        {
            TexturePreset.MaximumDetail => "Batched Real-ESRGAN maximum-detail reconstruction with TTA",
            TexturePreset.Illustrated =>
                key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                    ? $"Batched texture-specialized illustrated reconstruction ({key.ModelName}) with {FormatPaintedTheme(job.Request.Options.PaintedTheme)} finishing{FormatGraphicPaintedScale(graphicPaintedScale)}{FormatPaintedThemeScale(paintedThemeScale)}"
                    : $"Batched legacy illustrated reconstruction ({key.ModelName}) with {FormatPaintedTheme(job.Request.Options.PaintedTheme)} finishing{FormatGraphicPaintedScale(graphicPaintedScale)}{FormatPaintedThemeScale(paintedThemeScale)}",
            TexturePreset.RusticPainted =>
                key.WorkerKind == NeuralWorkerKind.TextureSpecialized
                    ? $"Batched texture-specialized graphic painted reconstruction ({key.ModelName}) with bounded rustic grading"
                    : "Batched graphic painted reconstruction with bounded rustic grading",
            TexturePreset.Faithful when job.EffectivePreset == TexturePreset.Illustrated =>
                $"Batched conservative illustrated recovery ({key.ModelName}) with {FormatPaintedTheme(job.Request.Options.PaintedTheme)} finishing{FormatGraphicPaintedScale(graphicPaintedScale)}{FormatPaintedThemeScale(paintedThemeScale)}",
            TexturePreset.Faithful when job.EffectivePreset != TexturePreset.Faithful =>
                "Batched faithful Real-ESRNet reconstruction (fidelity fallback)",
            _ => "Batched Real-ESRNet faithful color restoration with seam-safe border"
        };
    }

    private static IReadOnlyList<IReadOnlyList<PreparedColorJob>> ChunkNeuralJobs(
        IEnumerable<PreparedColorJob> jobs)
    {
        var chunks = new List<IReadOnlyList<PreparedColorJob>>();
        var current = new List<PreparedColorJob>();
        long currentOutputPixels = 0;
        foreach (var job in jobs.OrderBy(candidate => candidate.RequestIndex))
        {
            var outputPixels = EstimateNeuralOutputPixels(job.Request, job.Dimensions);
            if (current.Count > 0
                && (current.Count >= MaximumNeuralBatchItems
                    || currentOutputPixels + outputPixels > MaximumNeuralBatchOutputPixels))
            {
                chunks.Add(current.ToArray());
                current.Clear();
                currentOutputPixels = 0;
            }

            current.Add(job);
            currentOutputPixels = checked(currentOutputPixels + outputPixels);
        }

        if (current.Count > 0)
        {
            chunks.Add(current.ToArray());
        }

        return chunks;
    }

    private static long EstimateNeuralOutputPixels(
        NativeTextureProcessRequest request,
        UpscaleDimensions dimensions)
    {
        var neuralScale = ShouldUseTextureSpecializedReconstruction(request.Options.Preset)
            ? UpscaylCommandBuilder.ModelScale
            : dimensions.RequiredNeuralScale;
        var borderedWidth = checked(
            request.Metadata.Width + (request.WrapPadding * 2));
        var borderedHeight = checked(
            request.Metadata.Height + (request.WrapPadding * 2));
        return checked(
            (long)borderedWidth
            * borderedHeight
            * neuralScale
            * neuralScale);
    }

    internal static string SelectNeuralWorkerThreads(
        int processorCount,
        int jobCount,
        long maximumOutputPixels)
    {
        if (processorCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processorCount));
        }

        if (jobCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(jobCount));
        }

        if (maximumOutputPixels <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumOutputPixels));
        }

        // Three processing/save lanes improve directories containing many
        // small textures on high-thread-count systems. Large images retain the
        // lower-memory profile, and a native failure is retried conservatively
        // before the normal faithful-model fallback is considered.
        return processorCount >= 12
               && jobCount >= 6
               && maximumOutputPixels <= 2L * 1024 * 1024
            ? AcceleratedSmallTextureWorkerThreads
            : ConservativeNeuralWorkerThreads;
    }

    public async Task CreatePngPreviewAsync(
        string sourcePath,
        string destinationPath,
        int? width = null,
        int? height = null,
        bool nearestNeighbor = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("Preview source texture was not found.", sourcePath);
        }

        EnsureDirectXTex();
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath))
            ?? throw new InvalidOperationException("Preview destination has no parent directory.");
        Directory.CreateDirectory(destinationDirectory);
        var temporaryDirectory = Path.Combine(destinationDirectory, $".preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporaryDirectory);
        try
        {
            await RunCheckedAsync(
                _directXTex.CreateConvert(
                    _tools.TexconvPath!,
                    sourcePath,
                    temporaryDirectory,
                    "png",
                    width,
                    height,
                    wrapSampling: false,
                    resizeFilter: nearestNeighbor ? "POINT" : "CUBIC"),
                nativeProgress: null,
                cancellationToken).ConfigureAwait(false);
            await CopyFileAsync(
                FindSingleOutput(temporaryDirectory, ".png"),
                destinationPath,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteOwnedChildDirectory(destinationDirectory, temporaryDirectory, ".preview-");
        }
    }

    internal static void ValidateNeuralOutputForPreset(
        TexturePreset preset,
        TextureFidelityMetrics fidelity)
    {
        if (preset == TexturePreset.Illustrated)
        {
            ValidateGraphicPaintedOutput(fidelity);
            return;
        }

        if (preset == TexturePreset.RusticPainted)
        {
            ValidateStylizedOutput(fidelity);
            return;
        }

        ValidateNeuralOutput(fidelity);
    }

    private static void ValidateNeuralOutput(TextureFidelityMetrics fidelity)
    {
        if (fidelity.SourceStatistics.Samples == 0)
        {
            return;
        }

        if (fidelity.EnhancedStatistics.Samples == 0)
        {
            throw new InvalidDataException("The neural output contains no visible color pixels.");
        }

        if (fidelity.MeanLuminanceDrift > 6
            || fidelity.MaximumMeanChannelDrift > 12)
        {
            throw new InvalidDataException(
                "The neural output failed the faithful palette/luminance drift gate "
                + $"(luma {fidelity.MeanLuminanceDrift:0.00}, channel {fidelity.MaximumMeanChannelDrift:0.00}).");
        }

        if (fidelity.RoundTripLuminancePsnr < 27.5)
        {
            throw new InvalidDataException(
                "The neural output failed the round-trip structure gate "
                + $"({fidelity.RoundTripLuminancePsnr:0.00} dB PSNR)." );
        }

        if (fidelity.SourceEdgeEnergy >= 0.5
            && fidelity.EdgeEnergyRatio is < 0.65 or > 2.10)
        {
            throw new InvalidDataException(
                "The neural output failed the edge-energy fidelity gate "
                + $"({fidelity.EdgeEnergyRatio:0.00}x source)." );
        }

        if (fidelity.ExtremeLuminanceGrowth > 0.02)
        {
            throw new InvalidDataException(
                "The neural output introduced too many clipped black or white pixels "
                + $"(+{fidelity.ExtremeLuminanceGrowth:P1}).");
        }
    }

    private static void ValidateStylizedOutput(TextureFidelityMetrics fidelity)
    {
        if (fidelity.SourceStatistics.Samples == 0)
        {
            return;
        }

        if (fidelity.EnhancedStatistics.Samples == 0
            || fidelity.MeanLuminanceDrift > 10
            || fidelity.MaximumMeanChannelDrift > 20
            || fidelity.RoundTripLuminancePsnr < 24
            || (fidelity.SourceEdgeEnergy >= 0.5
                && fidelity.EdgeEnergyRatio is < 0.40 or > 2.15)
            || fidelity.ExtremeLuminanceGrowth > 0.02)
        {
            throw new InvalidDataException(
                "The stylized output exceeded its bounded fidelity limits "
                + $"(luma {fidelity.MeanLuminanceDrift:0.00}, "
                + $"channel {fidelity.MaximumMeanChannelDrift:0.00}, "
                + $"PSNR {fidelity.RoundTripLuminancePsnr:0.00} dB, "
                + $"edge {fidelity.EdgeEnergyRatio:0.00}x, "
                + $"clipping growth {fidelity.ExtremeLuminanceGrowth:P1}).");
        }
    }

    private static void ValidateGraphicPaintedOutput(TextureFidelityMetrics fidelity)
    {
        if (fidelity.SourceStatistics.Samples == 0)
        {
            return;
        }

        if (fidelity.EnhancedStatistics.Samples == 0
            || fidelity.MeanLuminanceDrift > 10
            || fidelity.MaximumMeanChannelDrift > 20
            // Graphic abstraction deliberately reorganizes fine texels, so a
            // faithful-mode PSNR floor rejects healthy illustrated output. Keep
            // a low catastrophic floor and require the coarse scene layout to
            // remain strongly correlated instead.
            || fidelity.RoundTripLuminancePsnr < 12.5
            || fidelity.CoarseLuminanceCorrelation < 0.72
            || (fidelity.SourceEdgeEnergy >= 0.5
                && fidelity.EdgeEnergyRatio is < 0.18 or > 2.25)
            || fidelity.ExtremeLuminanceGrowth > 0.02)
        {
            throw new InvalidDataException(
                "The graphic-painted output exceeded its bounded fidelity limits "
                + $"(luma {fidelity.MeanLuminanceDrift:0.00}, "
                + $"channel {fidelity.MaximumMeanChannelDrift:0.00}, "
                + $"PSNR {fidelity.RoundTripLuminancePsnr:0.00} dB, "
                + $"coarse structure {fidelity.CoarseLuminanceCorrelation:0.000}, "
                + $"edge {fidelity.EdgeEnergyRatio:0.00}x, "
                + $"clipping growth {fidelity.ExtremeLuminanceGrowth:P1}).");
        }
    }

    private static double GetGraphicPaintedStrength(
        NativeTextureProcessRequest request,
        bool preserveAlphaCoverage)
    {
        var strength = request.Options.Scope switch
        {
            // Animated effects benefit from cleaner shapes, but retaining their
            // timing silhouettes matters more than strong material flattening.
            AssetScope.SpellEffectsOnly => 0.44,
            AssetScope.CharactersAndEquipmentOnly => 0.72,
            AssetScope.WorldCharactersAndEquipment => 0.76,
            AssetScope.AllSafeTextures => 0.76,
            _ => 0.78
        };

        if (request.Classification.Kind == TextureKind.Cutout || preserveAlphaCoverage)
        {
            // Foliage, hair, fences, decals, and other alpha-tested cards rely
            // on fine internal edges as well as their outer silhouette. A
            // restrained finish keeps the requested painted plane treatment
            // without turning those small structures into broad soft blobs.
            strength *= 0.58;
        }

        return strength;
    }

    private static double GetRusticPaintedStrength(NativeTextureProcessRequest request)
    {
        var strength = request.Options.Scope switch
        {
            AssetScope.SpellEffectsOnly => 0.42,
            AssetScope.CharactersAndEquipmentOnly => 0.76,
            AssetScope.WorldCharactersAndEquipment => 0.86,
            AssetScope.AllSafeTextures => 0.86,
            _ => 1.0
        };

        if (request.Classification.Kind == TextureKind.Cutout)
        {
            strength *= 0.72;
        }

        return strength;
    }

    private static double GetPaintedThemeStrength(
        NativeTextureProcessRequest request,
        bool preserveAlphaCoverage)
    {
        var strength = request.Options.Scope switch
        {
            AssetScope.SpellEffectsOnly => 0.30,
            AssetScope.CharactersAndEquipmentOnly => 0.66,
            AssetScope.WorldCharactersAndEquipment => 0.72,
            AssetScope.AllSafeTextures => 0.70,
            _ => 0.78
        };

        if (request.Classification.Kind == TextureKind.Cutout || preserveAlphaCoverage)
        {
            strength *= 0.62;
        }

        return strength;
    }

    private static (TgaPixelBuffer Image, double StrengthScale) ApplyValidatedPaintedTheme(
        TgaPixelBuffer source,
        TgaPixelBuffer graphicPainted,
        PaintedTheme theme,
        double requestedStrength)
    {
        if (theme == PaintedTheme.ClassicPainted)
        {
            // The shared graphic-painted result was validated immediately
            // before this call. Classic adds no palette direction, so avoid a
            // redundant full-resolution clone and second fidelity scan.
            return (graphicPainted, 1d);
        }

        // The common graphic-painted reconstruction is already validated. A
        // palette direction may sit just beyond the aggregate gate on one
        // unusual texture, so reduce only that theme treatment before allowing
        // the normal faithful fallback to replace the complete illustrated
        // route. This keeps Comic Ink and the zone palettes visible far more
        // consistently without accepting unsafe output.
        double[] scales = [1d, 0.8d, 0.6d, 0.4d];
        InvalidDataException? lastFailure = null;
        foreach (var scale in scales)
        {
            var themed = graphicPainted.ApplyPaintedTheme(
                theme,
                requestedStrength * scale);
            try
            {
                ValidateGraphicPaintedOutput(CalculateFidelityMetrics(source, themed));
                return (themed, scale);
            }
            catch (InvalidDataException exception)
            {
                lastFailure = exception;
            }
        }

        throw new InvalidDataException(
            $"The {FormatPaintedTheme(theme)} treatment exceeded its bounded fidelity limits even at reduced strength.",
            lastFailure);
    }

    private static string FormatGraphicPaintedScale(double? scale) =>
        scale is { } value && value < 0.999d
            ? $" at {value:P0} detail-preserving finish strength"
            : string.Empty;

    private static string FormatPaintedThemeScale(double? scale) =>
        scale is { } value && value < 0.999d
            ? $" at {value:P0} safety strength"
            : string.Empty;

    private static string FormatPaintedTheme(PaintedTheme theme) => theme switch
    {
        PaintedTheme.ClassicPainted => "classic graphic-painted",
        PaintedTheme.LightStorybook => "light storybook",
        PaintedTheme.DarkGothic => "dark gothic",
        PaintedTheme.ComicInk => "comic ink",
        _ => throw new InvalidDataException(
            "A concrete painted theme was not resolved before native processing.")
    };

    internal static TextureFidelityMetrics CalculateFidelityMetrics(
        TgaPixelBuffer source,
        TgaPixelBuffer enhanced)
    {
        var horizontalScale = enhanced.Width / (double)source.Width;
        var verticalScale = enhanced.Height / (double)source.Height;
        if (horizontalScale <= 0 || verticalScale <= 0)
        {
            throw new InvalidDataException("The enhanced texture dimensions are invalid.");
        }

        // A maximum-dimension cap commonly rounds one axis by a fraction of a
        // pixel (for example 300x200 -> 1024x683). Treat that as the same
        // aspect ratio, while continuing to reject actual geometric distortion.
        var expectedHeightFromWidth = source.Height * horizontalScale;
        if (Math.Abs(enhanced.Height - expectedHeightFromWidth) > 1.01)
        {
            throw new InvalidDataException(
                "The enhanced texture scale changes the source aspect ratio.");
        }

        var isExactIntegerScale = enhanced.Width % source.Width == 0
            && enhanced.Height % source.Height == 0
            && enhanced.Width / source.Width == enhanced.Height / source.Height;
        var integerScale = isExactIntegerScale
            ? enhanced.Width / source.Width
            : 0;

        var sourceStatistics = source.CalculateVisibleColorStatistics();
        var enhancedStatistics = enhanced.CalculateVisibleColorStatistics();
        var sourcePixels = source.RgbaPixels.Span;
        var enhancedPixels = enhanced.RgbaPixels.Span;
        var sourceLuminance = new double[checked(source.Width * source.Height)];
        var reducedLuminance = new double[sourceLuminance.Length];
        var visible = new bool[sourceLuminance.Length];
        double squaredError = 0;
        long samples = 0;

        for (var sourceY = 0; sourceY < source.Height; sourceY++)
        {
            for (var sourceX = 0; sourceX < source.Width; sourceX++)
            {
                var sourcePixel = (sourceY * source.Width) + sourceX;
                var sourceOffset = sourcePixel * 4;
                var sourceLuma = Luminance(sourcePixels, sourceOffset);
                sourceLuminance[sourcePixel] = sourceLuma;
                visible[sourcePixel] = sourcePixels[sourceOffset + 3] >= 8;

                double reducedRed;
                double reducedGreen;
                double reducedBlue;
                if (isExactIntegerScale)
                {
                    reducedRed = 0;
                    reducedGreen = 0;
                    reducedBlue = 0;
                    for (var blockY = 0; blockY < integerScale; blockY++)
                    {
                        var enhancedY = (sourceY * integerScale) + blockY;
                        for (var blockX = 0; blockX < integerScale; blockX++)
                        {
                            var enhancedX = (sourceX * integerScale) + blockX;
                            var enhancedOffset = ((enhancedY * enhanced.Width) + enhancedX) * 4;
                            reducedRed += enhancedPixels[enhancedOffset];
                            reducedGreen += enhancedPixels[enhancedOffset + 1];
                            reducedBlue += enhancedPixels[enhancedOffset + 2];
                        }
                    }

                    var blockSamples = integerScale * integerScale;
                    reducedRed /= blockSamples;
                    reducedGreen /= blockSamples;
                    reducedBlue /= blockSamples;
                }
                else
                {
                    // Deterministic area reduction compares the exact final
                    // encoded size to the source even when one capped axis was
                    // rounded. The integer fast path above remains byte-for-byte
                    // equivalent to the established fidelity calculation.
                    var left = sourceX * horizontalScale;
                    var right = (sourceX + 1) * horizontalScale;
                    var top = sourceY * verticalScale;
                    var bottom = (sourceY + 1) * verticalScale;
                    var firstX = Math.Max(0, (int)Math.Floor(left));
                    var lastX = Math.Min(enhanced.Width - 1, (int)Math.Ceiling(right) - 1);
                    var firstY = Math.Max(0, (int)Math.Floor(top));
                    var lastY = Math.Min(enhanced.Height - 1, (int)Math.Ceiling(bottom) - 1);
                    double weightedRed = 0;
                    double weightedGreen = 0;
                    double weightedBlue = 0;
                    double totalWeight = 0;
                    for (var enhancedY = firstY; enhancedY <= lastY; enhancedY++)
                    {
                        var verticalWeight = Math.Max(
                            0,
                            Math.Min(bottom, enhancedY + 1d) - Math.Max(top, enhancedY));
                        for (var enhancedX = firstX; enhancedX <= lastX; enhancedX++)
                        {
                            var horizontalWeight = Math.Max(
                                0,
                                Math.Min(right, enhancedX + 1d) - Math.Max(left, enhancedX));
                            var weight = horizontalWeight * verticalWeight;
                            var enhancedOffset = ((enhancedY * enhanced.Width) + enhancedX) * 4;
                            weightedRed += enhancedPixels[enhancedOffset] * weight;
                            weightedGreen += enhancedPixels[enhancedOffset + 1] * weight;
                            weightedBlue += enhancedPixels[enhancedOffset + 2] * weight;
                            totalWeight += weight;
                        }
                    }

                    if (totalWeight <= 0)
                    {
                        throw new InvalidDataException(
                            "The enhanced texture could not be reduced to the source grid.");
                    }

                    reducedRed = weightedRed / totalWeight;
                    reducedGreen = weightedGreen / totalWeight;
                    reducedBlue = weightedBlue / totalWeight;
                }

                var reducedLuma = (0.2126 * reducedRed)
                    + (0.7152 * reducedGreen)
                    + (0.0722 * reducedBlue);
                reducedLuminance[sourcePixel] = reducedLuma;
                if (visible[sourcePixel])
                {
                    var difference = reducedLuma - sourceLuma;
                    squaredError += difference * difference;
                    samples++;
                }
            }
        }

        double sourceEdgeEnergy = 0;
        double enhancedEdgeEnergy = 0;
        long edgeSamples = 0;
        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var index = (y * source.Width) + x;
                if (!visible[index])
                {
                    continue;
                }

                if (x + 1 < source.Width && visible[index + 1])
                {
                    sourceEdgeEnergy += Math.Abs(sourceLuminance[index] - sourceLuminance[index + 1]);
                    enhancedEdgeEnergy += Math.Abs(reducedLuminance[index] - reducedLuminance[index + 1]);
                    edgeSamples++;
                }

                if (y + 1 < source.Height && visible[index + source.Width])
                {
                    sourceEdgeEnergy += Math.Abs(
                        sourceLuminance[index] - sourceLuminance[index + source.Width]);
                    enhancedEdgeEnergy += Math.Abs(
                        reducedLuminance[index] - reducedLuminance[index + source.Width]);
                    edgeSamples++;
                }
            }
        }

        var meanSquaredError = samples == 0 ? 0 : squaredError / samples;
        var psnr = meanSquaredError == 0
            ? double.PositiveInfinity
            : 10 * Math.Log10((255d * 255d) / meanSquaredError);
        var meanSourceEdge = edgeSamples == 0 ? 0 : sourceEdgeEnergy / edgeSamples;
        var meanEnhancedEdge = edgeSamples == 0 ? 0 : enhancedEdgeEnergy / edgeSamples;
        var edgeRatio = meanSourceEdge < 0.001 ? 1 : meanEnhancedEdge / meanSourceEdge;
        var maximumChannelDrift = Math.Max(
            Math.Abs(enhancedStatistics.MeanRed - sourceStatistics.MeanRed),
            Math.Max(
                Math.Abs(enhancedStatistics.MeanGreen - sourceStatistics.MeanGreen),
                Math.Abs(enhancedStatistics.MeanBlue - sourceStatistics.MeanBlue)));
        var coarseLuminanceCorrelation = CalculateCoarseLuminanceCorrelation(
            sourceLuminance,
            reducedLuminance,
            visible,
            source.Width,
            source.Height);

        return new TextureFidelityMetrics(
            sourceStatistics,
            enhancedStatistics,
            Math.Abs(enhancedStatistics.MeanLuminance - sourceStatistics.MeanLuminance),
            maximumChannelDrift,
            meanSquaredError,
            psnr,
            coarseLuminanceCorrelation,
            meanSourceEdge,
            edgeRatio,
            Math.Max(
                0,
                enhancedStatistics.ExtremeLuminanceFraction
                - sourceStatistics.ExtremeLuminanceFraction));
    }

    private static double CalculateCoarseLuminanceCorrelation(
        double[] source,
        double[] enhanced,
        bool[] visible,
        int width,
        int height)
    {
        const int maximumGrid = 8;
        var columns = Math.Min(maximumGrid, width);
        var rows = Math.Min(maximumGrid, height);
        var sourceCells = new List<double>(checked(columns * rows));
        var enhancedCells = new List<double>(checked(columns * rows));
        for (var cellY = 0; cellY < rows; cellY++)
        {
            var top = (cellY * height) / rows;
            var bottom = ((cellY + 1) * height) / rows;
            for (var cellX = 0; cellX < columns; cellX++)
            {
                var left = (cellX * width) / columns;
                var right = ((cellX + 1) * width) / columns;
                double sourceSum = 0;
                double enhancedSum = 0;
                var samples = 0;
                for (var y = top; y < bottom; y++)
                {
                    for (var x = left; x < right; x++)
                    {
                        var index = (y * width) + x;
                        if (!visible[index])
                        {
                            continue;
                        }

                        sourceSum += source[index];
                        enhancedSum += enhanced[index];
                        samples++;
                    }
                }

                if (samples > 0)
                {
                    sourceCells.Add(sourceSum / samples);
                    enhancedCells.Add(enhancedSum / samples);
                }
            }
        }

        if (sourceCells.Count < 2)
        {
            return 1;
        }

        var sourceMean = sourceCells.Average();
        var enhancedMean = enhancedCells.Average();
        double covariance = 0;
        double sourceVariance = 0;
        double enhancedVariance = 0;
        for (var index = 0; index < sourceCells.Count; index++)
        {
            var sourceDelta = sourceCells[index] - sourceMean;
            var enhancedDelta = enhancedCells[index] - enhancedMean;
            covariance += sourceDelta * enhancedDelta;
            sourceVariance += sourceDelta * sourceDelta;
            enhancedVariance += enhancedDelta * enhancedDelta;
        }

        if (sourceVariance < 0.001)
        {
            // A source with no coarse spatial structure cannot meaningfully be
            // correlated; its palette, clipping and PSNR gates remain active.
            return 1;
        }

        if (enhancedVariance < 0.001)
        {
            return 0;
        }

        return covariance / Math.Sqrt(sourceVariance * enhancedVariance);
    }

    private static double Luminance(ReadOnlySpan<byte> rgba, int offset) =>
        (0.2126 * rgba[offset]) + (0.7152 * rgba[offset + 1]) + (0.0722 * rgba[offset + 2]);

    private async Task<GeneratedMipResult> ProcessDataTextureAsync(
        NativeTextureProcessRequest request,
        UpscaleDimensions dimensions,
        string operationDirectory,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        EnsureDirectXTex();
        if (request.Classification.Kind == TextureKind.Normal)
        {
            var decodedDirectory = CreateDirectory(operationDirectory, "normal-decoded");
            await RunCheckedAsync(
                _directXTex.CreateConvert(
                    _tools.TexconvPath!,
                    request.SourcePath,
                    decodedDirectory,
                    "tga",
                    dimensions.OutputWidth,
                    dimensions.OutputHeight,
                    wrapSampling: true),
                nativeProgress,
                cancellationToken).ConfigureAwait(false);
            var resizedPath = FindSingleOutput(decodedDirectory, ".tga");
            var resized = await TgaPixelBuffer.ReadFileAsync(resizedPath, cancellationToken).ConfigureAwait(false);
            var reconstructZ = request.Metadata.TexconvFormat?.StartsWith("BC5", StringComparison.OrdinalIgnoreCase) == true;
            var normalizedPath = Path.Combine(operationDirectory, "normal-renormalized.tga");
            await resized.RenormalizeNormals(reconstructZ)
                .WriteFileAsync(normalizedPath, cancellationToken)
                .ConfigureAwait(false);
            return await EncodeToDestinationAsync(
                request,
                dimensions,
                normalizedPath,
                operationDirectory,
                nativeProgress,
                request.Classification.Kind == TextureKind.Cutout,
                cancellationToken).ConfigureAwait(false);
        }

        return await EncodeToDestinationAsync(
            request,
            dimensions,
            request.SourcePath,
            operationDirectory,
            nativeProgress,
            request.Classification.Kind == TextureKind.Cutout,
            cancellationToken).ConfigureAwait(false);
    }

    private async Task ValidateFinalIllustratedOutputAsync(
        NativeTextureProcessRequest request,
        TgaPixelBuffer source,
        string operationDirectory,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        // Validation before encoding cannot see detail lost to the destination
        // palette or block compression. Decode the bytes that will actually be
        // placed in the archive and run the same painted structure gate again.
        // This is deliberately Illustrated-only: other profiles retain their
        // established fidelity identity and performance characteristics.
        var validationDirectory = CreateDirectory(
            operationDirectory,
            "illustrated-final-validation");
        await RunCheckedAsync(
            _directXTex.CreateConvert(
                _tools.TexconvPath!,
                request.DestinationPath,
                validationDirectory,
                "tga"),
            nativeProgress,
            cancellationToken).ConfigureAwait(false);
        var decodedPath = FindSingleOutput(validationDirectory, ".tga");
        var finalOutput = await TgaPixelBuffer
            .ReadFileAsync(decodedPath, cancellationToken)
            .ConfigureAwait(false);
        try
        {
            ValidateGraphicPaintedOutput(CalculateFidelityMetrics(source, finalOutput));
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidDataException(
                "The final encoded graphic-painted texture lost too much source structure or detail.",
                exception);
        }
    }

    internal static bool RequiresFinalIllustratedDecodeValidation(
        TextureMetadata metadata,
        TextureClassification classification,
        bool preserveAlphaCoverage)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(classification);
        return classification.Kind == TextureKind.Cutout
            || preserveAlphaCoverage
            || (metadata.FileFormat == TextureFileFormat.Bmp
                && metadata.BitsPerPixel == 8);
    }

    private async Task<GeneratedMipResult> EncodeToDestinationAsync(
        NativeTextureProcessRequest request,
        UpscaleDimensions dimensions,
        string inputPath,
        string operationDirectory,
        IProgress<NativeOutputLine>? nativeProgress,
        bool preserveAlphaCoverage,
        CancellationToken cancellationToken,
        string resizeFilter = "CUBIC")
    {
        if (request.Metadata.FileFormat == TextureFileFormat.Bmp
            && request.Metadata.BitsPerPixel == 8)
        {
            var indexedInputPath = inputPath;
            string? resizedDirectory = null;
            var inputMetadata = await new TextureHeaderSniffer()
                .ReadFileAsync(inputPath, cancellationToken)
                .ConfigureAwait(false);
            if (inputMetadata is null
                || inputMetadata.Width != dimensions.OutputWidth
                || inputMetadata.Height != dimensions.OutputHeight)
            {
                resizedDirectory = CreateDirectory(operationDirectory, "indexed-bmp-resized");
                await RunCheckedAsync(
                    _directXTex.CreateConvert(
                        _tools.TexconvPath!,
                        inputPath,
                        resizedDirectory,
                        "tga",
                        dimensions.OutputWidth,
                        dimensions.OutputHeight,
                        wrapSampling: request.WrapEdges,
                        resizeFilter: resizeFilter),
                    nativeProgress,
                    cancellationToken).ConfigureAwait(false);
                indexedInputPath = FindSingleOutput(resizedDirectory, ".tga");
            }

            var enhanced = await TgaPixelBuffer
                .ReadFileAsync(indexedInputPath, cancellationToken)
                .ConfigureAwait(false);
            var sourceBytes = await File
                .ReadAllBytesAsync(request.SourcePath, cancellationToken)
                .ConfigureAwait(false);
            var indexedBytes = LegacyIndexedBmp.Encode(
                sourceBytes,
                enhanced.Width,
                enhanced.Height,
                enhanced.RgbaPixels.Span);
            await File.WriteAllBytesAsync(
                request.DestinationPath,
                indexedBytes,
                cancellationToken).ConfigureAwait(false);
            return new GeneratedMipResult(1, UsedCutoutFloor: false);
        }

        var usedCutoutMipFloor = request.Metadata.FileFormat == TextureFileFormat.Dds
            && preserveAlphaCoverage;
        var mipCount = request.Metadata.FileFormat == TextureFileFormat.Dds
            ? TextureMipPolicy.Calculate(
                dimensions.OutputWidth,
                dimensions.OutputHeight,
                request.Options.GenerateMipMaps,
                usedCutoutMipFloor)
            : 1;
        var encodedDirectory = CreateDirectory(operationDirectory, "encoded");
        await RunCheckedAsync(
            _directXTex.CreateEncode(
                _tools.TexconvPath!,
                inputPath,
                encodedDirectory,
                request.Metadata,
                dimensions.OutputWidth,
                dimensions.OutputHeight,
                mipCount,
                preserveAlphaCoverage,
                wrapSampling: request.WrapEdges,
                resizeFilter: resizeFilter),
            nativeProgress,
            cancellationToken).ConfigureAwait(false);

        var extension = request.Metadata.FileFormat switch
        {
            TextureFileFormat.Dds => ".dds",
            TextureFileFormat.Bmp => ".bmp",
            TextureFileFormat.Tga => ".tga",
            _ => throw new ArgumentOutOfRangeException(nameof(request))
        };
        var encodedPath = FindSingleOutput(encodedDirectory, extension);
        await CopyFileAsync(encodedPath, request.DestinationPath, cancellationToken).ConfigureAwait(false);
        return new GeneratedMipResult(mipCount, usedCutoutMipFloor);
    }

    private async Task RunCheckedAsync(
        NativeProcessCommand command,
        IProgress<NativeOutputLine>? nativeProgress,
        CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(command, nativeProgress, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            var diagnostic = string.IsNullOrWhiteSpace(result.StandardError)
                ? result.StandardOutput
                : result.StandardError;
            diagnostic = diagnostic.Trim();
            if (diagnostic.Length > 4_000)
            {
                diagnostic = diagnostic[^4_000..];
            }

            throw new NativeProcessException(
                $"{command.DisplayName ?? command.ExecutablePath} exited with code {result.ExitCode}."
                + (diagnostic.Length == 0
                    ? string.Empty
                    : $"{Environment.NewLine}{diagnostic}"),
                result);
        }
    }

    private void EnsureDirectXTex()
    {
        if (_tools.TexconvPath is null)
        {
            throw new InvalidOperationException("DirectXTex texconv is required but was not discovered.");
        }
    }

    private void EnsureColorTools()
    {
        EnsureDirectXTex();
        if (_tools.RealEsrganPath is null || _tools.RealEsrganModelsPath is null)
        {
            throw new InvalidOperationException("Real-ESRGAN and its model directory are required for color processing.");
        }
    }

    private static void ValidateRequest(NativeTextureProcessRequest request)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.WorkingDirectory);
        ArgumentNullException.ThrowIfNull(request.Metadata);
        ArgumentNullException.ThrowIfNull(request.Classification);
        ArgumentNullException.ThrowIfNull(request.Options);

        if (!File.Exists(request.SourcePath))
        {
            throw new FileNotFoundException("Source texture was not found.", request.SourcePath);
        }

        if (request.WrapPadding < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request.WrapPadding));
        }

        if (!Enum.IsDefined(request.Options.PaintedTheme)
            || request.Options.PaintedTheme == PaintedTheme.ZoneAware
            || (request.Options.Preset != TexturePreset.Illustrated
                && request.Options.PaintedTheme != PaintedTheme.ClassicPainted))
        {
            throw new InvalidDataException(
                "Native texture processing requires one concrete painted theme for Graphic Painted Fantasy only.");
        }
    }

    private static string CreateDirectory(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        Directory.CreateDirectory(path);
        return path;
    }

    private static string FindSingleOutput(string directory, string extension)
    {
        var outputs = Directory
            .EnumerateFiles(directory, $"*{extension}", SearchOption.TopDirectoryOnly)
            .ToArray();
        return outputs.Length switch
        {
            1 => outputs[0],
            0 => throw new FileNotFoundException($"The native tool did not create a {extension} output in {directory}."),
            _ => throw new InvalidDataException($"The native tool created multiple {extension} outputs in {directory}.")
        };
    }

    private static async Task CopyFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            1024 * 128,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task CopyTemporaryFileAsync(
        string sourcePath,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        var destinationDirectory = Path.GetDirectoryName(Path.GetFullPath(destinationPath));
        if (!string.IsNullOrWhiteSpace(destinationDirectory))
        {
            Directory.CreateDirectory(destinationDirectory);
        }

        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static bool IsPerTextureFailure(Exception exception) => exception is
        NativeProcessException
        or NativeProcessInactivityException
        or InvalidDataException
        or NotSupportedException;

    private static void CleanupPreparedJobDirectories(
        string batchDirectory,
        IEnumerable<PreparedColorJob> jobs)
    {
        foreach (var job in jobs)
        {
            TryDeleteOwnedChildDirectory(batchDirectory, job.OperationDirectory, "job-");
        }
    }

    private static void TryDeleteOperationDirectory(string workingDirectory, string operationDirectory)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workingDirectory));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(operationDirectory));
            var relative = Path.GetRelativePath(root, target);
            if (!relative.StartsWith("texture-", StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar)
                || relative.Contains(Path.AltDirectorySeparatorChar))
            {
                return;
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Intermediate cleanup is best effort; staged payload validation remains authoritative.
        }
    }

    private static void TryDeleteOwnedChildDirectory(string rootDirectory, string targetDirectory, string prefix)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetDirectory));
            var relative = Path.GetRelativePath(root, target);
            if (!relative.StartsWith(prefix, StringComparison.Ordinal)
                || relative.Contains(Path.DirectorySeparatorChar)
                || relative.Contains(Path.AltDirectorySeparatorChar))
            {
                return;
            }

            if (Directory.Exists(target))
            {
                Directory.Delete(target, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Preview cleanup is best effort inside the build-owned staging directory.
        }
    }

    private static void TryDeleteOwnedFile(string rootDirectory, string targetPath)
    {
        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootDirectory));
            var target = Path.GetFullPath(targetPath);
            var relative = Path.GetRelativePath(root, target);
            if (Path.IsPathRooted(relative)
                || relative.Equals("..", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal)
                || relative.StartsWith($"..{Path.AltDirectorySeparatorChar}", StringComparison.Ordinal))
            {
                return;
            }

            if (File.Exists(target))
            {
                File.Delete(target);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The retry will report a useful native error if a stale output blocks it.
        }
    }

    private enum NeuralWorkerKind
    {
        TextureSpecialized,
        LegacyRealEsrgan
    }

    private sealed record NeuralBatchKey(
        NeuralWorkerKind WorkerKind,
        string ModelName,
        TexturePreset WorkerPreset,
        int NeuralScale,
        int TileSize);

    private sealed record PendingColorJob(
        int RequestIndex,
        NativeTextureProcessRequest Request,
        UpscaleDimensions Dimensions,
        string OperationDirectory,
        string LocalSourcePath,
        DateTimeOffset Started);

    private sealed record PreparedColorJob(
        int RequestIndex,
        NativeTextureProcessRequest Request,
        UpscaleDimensions Dimensions,
        string OperationDirectory,
        string NeuralInputPath,
        TgaPixelBuffer Decoded,
        bool PreserveAlphaCoverage,
        TexturePreset EffectivePreset,
        bool HasSoftTranslucentAlpha,
        DateTimeOffset Started);

    private sealed record GeneratedMipResult(
        int MipCount,
        bool UsedCutoutFloor);

    private sealed record NeuralBatchInvocation(
        string GroupDirectory,
        IReadOnlyDictionary<int, string> OutputPaths);

    private sealed record ColorJobCompletion(
        NativeTextureProcessResult? Result,
        Exception? Error);

    internal sealed record TextureFidelityMetrics(
        TextureColorStatistics SourceStatistics,
        TextureColorStatistics EnhancedStatistics,
        double MeanLuminanceDrift,
        double MaximumMeanChannelDrift,
        double RoundTripMeanSquaredError,
        double RoundTripLuminancePsnr,
        double CoarseLuminanceCorrelation,
        double SourceEdgeEnergy,
        double EdgeEnergyRatio,
        double ExtremeLuminanceGrowth);

    private sealed class NeuralBatchExecutionState
    {
        public int NextGroupId { get; set; }
    }
}
