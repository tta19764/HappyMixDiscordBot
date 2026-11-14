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
// Header menus (theme, language): one open at a time, closed by Escape or a click outside.
const menus = [...document.querySelectorAll("[data-menu]")];
for (const menu of menus)
    menu.addEventListener("toggle", () => { if (menu.open) for (const other of menus) if (other !== menu) other.open = false; });
document.addEventListener("click", event => { for (const menu of menus) if (menu.open && !menu.contains(event.target)) menu.open = false; });
document.addEventListener("keydown", event => {
    if (event.key !== "Escape") return;
    const open = menus.find(menu => menu.open);
    if (open) { open.open = false; open.querySelector("summary").focus(); }
});

// Theme: switch in place and remember it in the same cookie the server reads; "system" follows the OS setting.
document.querySelector("[data-theme-form]")?.addEventListener("submit", event => {
    const theme = event.submitter?.value;
    if (!theme) return;
    event.preventDefault();
    const root = document.documentElement;
    const secure = location.protocol === "https:" ? "; Secure" : "";
    if (theme === "system") {
        root.removeAttribute("data-theme");
        document.cookie = `hm_theme=; Path=/; Max-Age=0; SameSite=Lax${secure}`;
    } else {
        root.dataset.theme = theme;
        document.cookie = `hm_theme=${theme}; Path=/; Max-Age=31536000; SameSite=Lax${secure}`;
    }
    document.querySelector('meta[name="color-scheme"]')?.setAttribute("content", theme === "system" ? "dark light" : theme);
    for (const meta of document.querySelectorAll("meta[data-theme-color]"))
        meta.media = theme === "system" ? `(prefers-color-scheme: ${meta.dataset.themeColor})` : meta.dataset.themeColor === theme ? "all" : "not all";
    const form = event.currentTarget;
    for (const button of form.querySelectorAll("[name=theme]")) button.setAttribute("aria-pressed", String(button.value === theme));
    for (const icon of document.querySelectorAll("[data-theme-icon]")) icon.hidden = icon.dataset.themeIcon !== theme;
    const summary = document.querySelector("[data-theme-summary]");
    summary.setAttribute("aria-label", `${form.getAttribute("aria-label")}: ${event.submitter.textContent.trim()}`);
    form.closest("[data-menu]").open = false;
    summary.focus();
});

// Weekly day editor: a 30-hour track (the day plus the early hours of the next) drawn over the server-rendered
// range list. The list's inputs stay the source of truth, so saving and the no-JavaScript form behave the same.
for (const editor of document.querySelectorAll("[data-week-editor]")) {
    const TRACK = 30, STEP = 0.25, MIN = 0.25;
    const list = editor.querySelector("[data-range-list]");
    const track = editor.querySelector("[data-track]");
    const empty = editor.querySelector("[data-track-empty]");
    const template = editor.querySelector("[data-range-template]");
    const addButton = editor.querySelector("[data-add-range]");
    const labels = editor.dataset;
    const blocks = new Map();
    let selected = null;
    let serial = 0;

    const toHours = value => value ? Number(value.slice(0, 2)) + Number(value.slice(3, 5)) / 60 : null;
    const toTime = hours => {
        let minutes = Math.round(hours * 60) % 1440;
        if (minutes < 0) minutes += 1440;
        return `${String(Math.floor(minutes / 60)).padStart(2, "0")}:${String(minutes % 60).padStart(2, "0")}`;
    };
    const snap = hours => Math.round(hours / STEP) * STEP;
    const clamp = (value, low, high) => Math.min(Math.max(value, low), high);
    const items = () => [...list.querySelectorAll("[data-range-item]")];
    const field = (item, name) => item.querySelector(`[data-range-${name}]`);
    const tentative = item => field(item, "kind").value === "tentative";

    const read = item => {
        const start = toHours(field(item, "start").value), end = toHours(field(item, "end").value);
        if (start === null || end === null) return null;
        if (start === 0 && end === 0) return { start: 0, end: 24, allDay: true };
        return { start, end: end <= start ? end + 24 : end, allDay: false };
    };
    const write = (item, start, end) => {
        field(item, "start").value = toTime(start);
        field(item, "end").value = toTime(end);
    };
    const duration = range => {
        const minutes = Math.round((range.end - range.start) * 60);
        return minutes % 60 ? `${Math.floor(minutes / 60)}h ${minutes % 60}m` : `${minutes / 60}h`;
    };
    const describe = (item, range) => {
        const kind = tentative(item) ? labels.labelTentative : labels.labelDefinite;
        if (range.allDay) return `${labels.labelAllDay} · ${kind}`;
        const next = range.end >= 24 ? `${labels.nextDay} ` : "";
        return `${toTime(range.start)} → ${next}${toTime(range.end)} · ${duration(range)} · ${kind}`;
    };

    // Neighbours limit dragging so ranges never overlap on the track.
    const bounds = item => {
        const range = read(item);
        let low = 0, high = TRACK;
        for (const other of items()) {
            if (other === item) continue;
            const span = read(other);
            if (!span) continue;
            if (span.start < range.start) low = Math.max(low, span.end);
            else high = Math.min(high, span.start);
        }
        return { low, high };
    };

    const place = (item, range) => {
        const block = blocks.get(item);
        block.style.left = `${range.start / TRACK * 100}%`;
        block.style.width = `${(range.end - range.start) / TRACK * 100}%`;
        block.classList.toggle("is-tentative", tentative(item));
        block.classList.toggle("is-selected", item === selected);
        block.setAttribute("aria-pressed", String(item === selected));
        const text = describe(item, range);
        block.setAttribute("aria-label", text);
        block.querySelector(".hm-weditor__label").textContent = range.allDay ? labels.labelAllDay : `${toTime(range.start)}–${toTime(range.end)}`;
        for (const [handle, value, label] of [[block.handles[0], range.start, labels.labelStart], [block.handles[1], range.end, labels.labelEnd]]) {
            handle.setAttribute("aria-valuenow", String(value));
            handle.setAttribute("aria-valuetext", `${label} ${toTime(value)}${value > 24 ? ` (${labels.nextDay})` : ""}`);
        }
        const preview = field(item, "preview-line");
        if (preview) preview.textContent = `${labels.day} · ${text}`;
        const summary = item.querySelector("[data-select-range]");
        summary.textContent = text;
    };

    const drag = (item, mode, event) => {
        event.preventDefault();
        event.stopPropagation();
        select(item, false);
        const start = read(item);
        const { low, high } = bounds(item);
        const rect = track.getBoundingClientRect();
        const origin = (event.clientX - rect.left) / rect.width * TRACK;
        const target = event.currentTarget;
        try { target.setPointerCapture(event.pointerId); } catch { /* synthetic or already released pointer */ }
        const move = moveEvent => {
            const at = (moveEvent.clientX - rect.left) / rect.width * TRACK;
            let { start: from, end: until } = start;
            if (mode === "start") from = clamp(snap(at), Math.max(low, until - 24 + (until > 24 ? MIN : 0)), Math.min(until - MIN, 24 - MIN));
            else if (mode === "end") until = clamp(snap(at), from + MIN, Math.min(high, from + 24 - (from === 0 ? 0 : MIN), TRACK));
            else {
                const length = until - from;
                from = clamp(snap(start.start + at - origin), low, Math.min(24 - MIN, high - length, TRACK - length));
                until = from + length;
            }
            write(item, from, until);
            place(item, read(item));
        };
        const stop = () => {
            target.removeEventListener("pointermove", move);
            target.removeEventListener("pointerup", stop);
            target.removeEventListener("pointercancel", stop);
            render();
        };
        target.addEventListener("pointermove", move);
        target.addEventListener("pointerup", stop);
        target.addEventListener("pointercancel", stop);
    };

    const nudge = (item, which, event) => {
        const steps = { ArrowLeft: -1, ArrowDown: -1, ArrowRight: 1, ArrowUp: 1 }[event.key];
        if (!steps && event.key !== "Home" && event.key !== "End") return;
        event.preventDefault();
        const range = read(item);
        const { low, high } = bounds(item);
        const delta = (steps ?? 0) * (event.shiftKey ? 1 : STEP);
        if (which === "start") {
            const min = Math.max(low, range.end - 24), max = Math.min(range.end - MIN, 24 - MIN);
            range.start = event.key === "Home" ? min : event.key === "End" ? max : clamp(range.start + delta, min, max);
        } else {
            const min = range.start + MIN, max = Math.min(high, range.start + 24 - (range.start === 0 ? 0 : MIN), TRACK);
            range.end = event.key === "Home" ? min : event.key === "End" ? max : clamp(range.end + delta, min, max);
        }
        write(item, range.start, range.end);
        place(item, read(item));
    };

    const blockFor = item => {
        if (blocks.has(item)) return blocks.get(item);
        const block = document.createElement("div");
        block.className = "hm-weditor__block";
        block.tabIndex = 0;
        block.setAttribute("role", "button");
        const label = document.createElement("span");
        label.className = "hm-weditor__label";
        block.append(label);
        block.handles = ["start", "end"].map(which => {
            const handle = document.createElement("span");
            handle.className = `hm-weditor__handle hm-weditor__handle--${which}`;
            handle.tabIndex = 0;
            handle.setAttribute("role", "slider");
            handle.setAttribute("aria-valuemin", "0");
            handle.setAttribute("aria-valuemax", String(TRACK));
            handle.setAttribute("aria-label", which === "start" ? labels.labelStart : labels.labelEnd);
            handle.addEventListener("pointerdown", event => drag(item, which, event));
            handle.addEventListener("keydown", event => nudge(item, which, event));
            handle.addEventListener("focus", () => { if (selected !== item) select(item, false); });
            block.append(handle);
            return handle;
        });
        block.addEventListener("pointerdown", event => {
            if (event.target !== block && event.target !== label) return;
            if (selected === item) drag(item, "move", event);
            else select(item);
        });
        block.addEventListener("keydown", event => {
            if (event.target !== block) return;
            if (event.key === "Enter" || event.key === " ") { event.preventDefault(); select(item); }
        });
        track.append(block);
        blocks.set(item, block);
        return block;
    };

    function render() {
        const current = items();
        for (const [item, block] of blocks)
            if (!current.includes(item)) { block.remove(); blocks.delete(item); }
        let shown = 0;
        for (const item of current) {
            item.classList.toggle("is-selected", item === selected);
            const summary = item.querySelector("[data-select-range]");
            summary.hidden = item === selected;
            const range = read(item);
            if (!range) {
                blocks.get(item)?.remove();
                blocks.delete(item);
                summary.textContent = "–";
                continue;
            }
            blockFor(item);
            place(item, range);
            shown++;
        }
        empty.hidden = shown > 0;
    }

    function select(item, focusFields = true) {
        selected = item;
        render();
        if (focusFields && item) field(item, "start").focus({ preventScroll: true });
        blocks.get(item)?.scrollIntoView({ block: "nearest", inline: "nearest" });
    }

    const prepare = item => {
        const suffix = `x${serial++}`;
        for (const element of item.querySelectorAll("[id]")) {
            const old = element.id;
            element.id = `${old}-${suffix}`;
            item.querySelector(`[for="${old}"]`)?.setAttribute("for", element.id);
            for (const described of item.querySelectorAll(`[aria-describedby="${old}"]`)) described.setAttribute("aria-describedby", element.id);
        }
        for (const button of item.querySelectorAll("[data-all-day], [data-remove-range], [data-select-range]")) button.hidden = false;
        item.querySelector("[data-select-range]").addEventListener("click", () => select(item));
        item.querySelector("[data-remove-range]").addEventListener("click", () => {
            const position = items().indexOf(item);
            item.remove();
            const rest = items();
            selected = rest[Math.min(position, rest.length - 1)] ?? null;
            render();
            (selected ? blocks.get(selected) : addButton)?.focus();
        });
        item.querySelector("[data-all-day]").addEventListener("click", () => {
            write(item, 0, 24);
            render();
        });
        return item;
    };

    // Start a new range at 18:00 when free, otherwise in the first free gap of an hour or more.
    const freeSlot = () => {
        const taken = items().map(read).filter(Boolean).sort((a, b) => a.start - b.start);
        const fits = (from, until) => taken.every(range => until <= range.start || from >= range.end);
        for (const from of [18, 19, 20, 21, 12, 9, 6, 0, 22, 23])
            for (const length of [3, 2, 1])
                if (fits(from, from + length)) return [from, from + length];
        return null;
    };

    addButton.addEventListener("click", () => {
        const slot = freeSlot();
        if (!slot) return;
        const item = prepare(template.content.firstElementChild.cloneNode(true));
        list.append(item);
        write(item, slot[0], slot[1]);
        select(item);
    });

    list.addEventListener("input", render);
    list.addEventListener("change", render);

    // Enhance: drop the empty fallback row, show the track and tools, and collapse unselected ranges.
    for (const item of items()) {
        if (item.dataset.blank === "true" && !read(item)) item.remove();
        else prepare(item);
    }
    editor.classList.add("is-enhanced");
    editor.querySelector("[data-track-board]").hidden = false;
    addButton.hidden = false;
    render();
}
