// HappyMix dashboard: progressive enhancement only. Every form works without this file.
(() => {
    const minutes = value => Number(value.slice(0, 2)) * 60 + Number(value.slice(3, 5));
    const duration = span => span % 60 ? `${Math.floor(span / 60)}h ${span % 60}m` : `${span / 60}h`;

    // Time range preview: "Today 17:00 → Tomorrow 00:30", overnight and all-day badges, duration, same-time error.
    for (const form of document.querySelectorAll("[data-range]")) {
        const select = name => form.querySelector(`[data-time-part="${name}"]`);
        const fields = ["start-hour", "start-minute", "end-hour", "end-minute"].map(select);
        const [startHour, startMinute, endHour, endMinute] = fields;
        const allDay = form.querySelector("[data-all-day]");
        const preview = form.querySelector("[data-range-preview]");
        const save = form.querySelector("[data-range-save]");
        const part = name => preview.querySelector(`[data-${name}]`);
        const time = (hour, minute) => hour.value ? `${hour.value}:${minute.value || "00"}` : "";
        const setTime = (hour, minute, value) => {
            hour.value = value.slice(0, 2);
            if (![...minute.options].some(option => option.value === value.slice(3, 5)))
                minute.add(new Option(value.slice(3, 5), value.slice(3, 5)));
            minute.value = value.slice(3, 5);
        };
        const render = () => {
            const whole = Boolean(allDay?.checked);
            for (const field of fields) field.disabled = whole;
            const start = whole ? "00:00" : time(startHour, startMinute);
            const end = whole ? "00:00" : time(endHour, endMinute);
            const ready = Boolean(start && end);
            const span = whole ? 1440 : ready ? (minutes(end) - minutes(start) + 1440) % 1440 : 0;
            const overnight = whole || (ready && span > 0 && end < start);
            part("hint").hidden = ready;
            part("ok").hidden = !ready || span === 0;
            part("invalid").hidden = !ready || span > 0;
            preview.classList.toggle("hm-preview--invalid", ready && span === 0);
            part("from").textContent = start;
            part("until").textContent = end;
            part("until-day").textContent = overnight ? form.dataset.nextDay : "";
            part("overnight").hidden = whole || !overnight;
            part("all-day-badge").hidden = !whole;
            part("duration").textContent = span ? duration(span) : "";
            if (save) save.disabled = ready && span === 0;
        };
        form.addEventListener("input", render);
        form.addEventListener("change", render);
        render();

        // Quick-fill presets only fill the selects; saving stays explicit.
        const presets = form.querySelector("[data-presets]");
        if (!presets) continue;
        presets.hidden = false;
        const unpress = () => { for (const chip of presets.querySelectorAll("[aria-pressed]")) chip.setAttribute("aria-pressed", "false"); };
        presets.addEventListener("click", event => {
            const preset = event.target.closest("[data-start]");
            if (!preset) return;
            if (allDay) allDay.checked = false;
            setTime(startHour, startMinute, preset.dataset.start);
            setTime(endHour, endMinute, preset.dataset.end);
            unpress();
            preset.setAttribute("aria-pressed", "true");
            render();
        });
        form.addEventListener("change", event => { if (fields.includes(event.target) || event.target === allDay) unpress(); });
    }

    // Day tabs: arrow keys, Home and End move between tabs; the URL keeps the chosen date.
    for (const tabs of document.querySelectorAll("[data-day-tabs]")) {
        const links = [...tabs.querySelectorAll("a")];
        tabs.addEventListener("keydown", event => {
            const index = links.indexOf(document.activeElement);
            if (index < 0) return;
            const next = { ArrowRight: index + 1, ArrowLeft: index - 1, Home: 0, End: links.length - 1 }[event.key];
            if (next === undefined) return;
            event.preventDefault();
            links[(next + links.length) % links.length].focus();
        });
        // Keep the selected tab in view inside the horizontal strip without scrolling the page.
        const current = tabs.querySelector("[aria-current]");
        if (current) tabs.scrollLeft = current.offsetLeft - (tabs.clientWidth - current.offsetWidth) / 2;
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

// Linked accounts: copy the one-time link.
for (const button of document.querySelectorAll("[data-copy]")) {
    const field = document.getElementById(button.dataset.copy);
    if (!field || !navigator.clipboard) continue;
    button.hidden = false;
    button.addEventListener("click", async () => {
        await navigator.clipboard.writeText(field.value);
        button.textContent = button.dataset.copied || "✓";
    });
}
document.querySelector("[data-language-select]")?.addEventListener("change", event => event.currentTarget.form.requestSubmit());
