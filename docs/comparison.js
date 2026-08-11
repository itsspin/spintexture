"use strict";

document.querySelectorAll("[data-comparison]").forEach((comparison) => {
  const range = comparison.querySelector("[data-comparison-range]");
  const output = document.querySelector("[data-position-output]");

  if (!(range instanceof HTMLInputElement)) {
    return;
  }

  const update = () => {
    const value = Math.min(100, Math.max(0, Number(range.value)));
    comparison.style.setProperty("--position", `${value}%`);
    range.setAttribute("aria-valuetext", `${value} percent original, ${100 - value} percent enhanced`);

    if (output instanceof HTMLOutputElement) {
      output.value = `${value}% reveal`;
    }
  };

  range.addEventListener("input", update);
  update();
});
