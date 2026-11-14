// HappyMix dashboard: progressive enhancement only. Every form works without this file.
(() => {
    const minutes = value => Number(value.slice(0, 2)) * 60 + Number(value.slice(3, 5));
    const duration = span => span % 60 ? `${Math.floor(span / 60)}h ${span % 60}m` : `${span / 60}h`;

    // Time range preview: "Today 17:00 → Tomorrow 00:30", overnight badge, duration, same-time error.
    for (const form of document.querySelectorAll("[data-range]")) {
        const { start, end } = form.elements;
        const preview = form.querySelector("[data-range-preview]");
        const save = form.querySelector("[data-range-save]");
        const part = name => preview.querySelector(`[data-${name}]`);
        const render = () => {
            const ready = Boolean(start.value && end.value);
            const span = ready ? (minutes(end.value) - minutes(start.value) + 1440) % 1440 : 0;
            const overnight = ready && span > 0 && end.value < start.value;
            part("hint").hidden = ready;
            part("ok").hidden = !ready || span === 0;
            part("invalid").hidden = !ready || span > 0;
            preview.classList.toggle("hm-preview--invalid", ready && span === 0);
            part("from").textContent = start.value;
            part("until").textContent = end.value;
            part("until-day").textContent = overnight ? form.dataset.nextDay : "";
            part("overnight").hidden = !overnight;
            part("duration").textContent = span ? duration(span) : "";
            if (save) save.disabled = ready && span === 0;
        };
        form.addEventListener("input", render);
        render();

        // Quick-fill presets only fill the inputs; saving stays explicit.
        const presets = form.querySelector("[data-presets]");
        if (!presets) continue;
        presets.hidden = false;
        presets.addEventListener("click", event => {
            const preset = event.target.closest("[data-start]");
            if (!preset) return;
            start.value = preset.dataset.start;
            end.value = preset.dataset.end;
            for (const chip of presets.querySelectorAll("[aria-pressed]"))
                chip.setAttribute("aria-pressed", String(chip === preset));
            render();
        });
        form.addEventListener("input", event => {
            if (event.target === start || event.target === end)
                for (const chip of presets.querySelectorAll("[aria-pressed]")) chip.setAttribute("aria-pressed", "false");
        });
    }

    // Settings: keep the Inherited / Custom / Off badge in sync with the control.
    const sources = { default: ["Inherited", "inherit"], custom: ["Custom", "custom"], none: ["Off", ""] };
    for (const field of document.querySelectorAll("[data-source]")) {
        const badge = field.querySelector("[data-source-badge]");
        field.addEventListener("change", () => {
            const select = field.querySelector("select[data-source-control]");
            const mode = select ? (select.value === "" ? "default" : "custom")
                : field.querySelector("input[type=radio]:checked")?.value ?? "default";
            const [text, tone] = sources[mode];
            badge.textContent = text;
            badge.className = "hm-badge" + (tone ? ` hm-badge--${tone}` : "");
            if (select) field.classList.toggle("hm-field--inherited", mode === "default");
        });
    }

    // Save bar: flag unsaved changes after the first edit.
    for (const form of document.querySelectorAll("[data-track-changes]"))
        form.addEventListener("input", () => { form.dataset.dirty = "true"; }, { once: true });

    // Move focus to a status or error banner rendered after a redirect so screen readers announce it.
    document.querySelector("[data-focus-on-load]")?.focus();
})();

// Confirmation panels: reveal the Cancel button and let Escape close an open panel.
for (const details of document.querySelectorAll("details.hm-confirm")) {
    const cancel = details.querySelector("[data-close-details]");
    if (cancel) {
        cancel.hidden = false;
        cancel.addEventListener("click", () => { details.open = false; details.querySelector("summary").focus(); });
    }
    details.addEventListener("keydown", event => {
        if (event.key === "Escape" && details.open) {
            details.open = false;
            details.querySelector("summary").focus();
        }
    });
}
