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
    const sources = { default: [0, "inherit"], custom: [1, "custom"], none: [2, ""] };
    for (const field of document.querySelectorAll("[data-source]")) {
        const badge = field.querySelector("[data-source-badge]");
        field.addEventListener("change", () => {
            const select = field.querySelector("select[data-source-control]");
            const mode = select ? (select.value === "" ? "default" : "custom")
                : field.querySelector("input[type=radio]:checked")?.value ?? "default";
            const [index, tone] = sources[mode];
            badge.textContent = (badge.dataset.labels || "Inherited|Custom|Off").split("|")[index];
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
for (const details of document.querySelectorAll("details.hm-confirm, details.hm-copyto")) {
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
    // The track shows the day plus the next morning (30h). It grows in 6h steps, up to the whole next day, when a
    // range ends later, so an overnight range never sticks out of the track (which used to scroll the card sideways).
    const BASE_TRACK = 30, MAX_TRACK = 48, STEP = 0.25, MIN = 0.25;
    let TRACK = BASE_TRACK;
    const list = editor.querySelector("[data-range-list]");
    const track = editor.querySelector("[data-track]");
    const empty = editor.querySelector("[data-track-empty]");
    const axis = editor.querySelector(".hm-weditor__axis");
    const nextDay = editor.querySelector(".hm-weditor__nextday");
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
    // From and Until are 24-hour hour/minute selects; "--" as the hour means the time is empty.
    const getTime = (item, which) => {
        const pair = field(item, which);
        const hour = pair.querySelector("[data-time-hour]").value;
        return hour ? `${hour}:${pair.querySelector("[data-time-minute]").value || "00"}` : "";
    };
    const setTime = (item, which, value) => {
        const pair = field(item, which);
        const [hour, minute] = value.split(":");
        const minutes = pair.querySelector("[data-time-minute]");
        if (![...minutes.options].some(option => option.value === minute)) {
            const option = new Option(minute, minute);
            const next = [...minutes.options].find(existing => existing.value > minute);
            minutes.add(option, next ?? null);
        }
        pair.querySelector("[data-time-hour]").value = hour;
        minutes.value = minute;
    };
    const tentative = item => field(item, "kind").value === "tentative";

    const read = item => {
        const start = toHours(getTime(item, "start")), end = toHours(getTime(item, "end"));
        if (start === null || end === null) return null;
        if (start === 0 && end === 0) return { start: 0, end: 24, allDay: true };
        return { start, end: end <= start ? end + 24 : end, allDay: false };
    };
    const write = (item, start, end) => {
        setTime(item, "start", toTime(start));
        setTime(item, "end", toTime(end));
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
        let low = 0, high = MAX_TRACK;
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
        let edgeTimer = 0, last = event;
        const target = event.currentTarget;
        try { target.setPointerCapture(event.pointerId); } catch { /* synthetic or already released pointer */ }
        const move = moveEvent => {
            last = moveEvent;
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
            // Holding a range at the right edge opens up 6 more hours of the next day every 400ms, like auto-scroll.
            const atEdge = mode !== "start" && moveEvent.clientX >= rect.right - 8 && TRACK < MAX_TRACK;
            if (atEdge && !edgeTimer)
                edgeTimer = setInterval(() => {
                    if (TRACK >= MAX_TRACK || last.clientX < rect.right - 8) { clearInterval(edgeTimer); edgeTimer = 0; return; }
                    setTrack(TRACK + 6);
                    move(last);
                }, 400);
        };
        const stop = () => {
            clearInterval(edgeTimer);
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
            const min = range.start + MIN, max = Math.min(high, range.start + 24 - (range.start === 0 ? 0 : MIN), MAX_TRACK);
            range.end = event.key === "Home" ? min : event.key === "End" ? max : clamp(range.end + delta, min, max);
        }
        write(item, range.start, range.end);
        setTrack(Math.max(TRACK, hoursNeeded()));
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

    const hoursNeeded = () => {
        const latest = Math.max(0, ...items().map(read).filter(Boolean).map(range => range.end));
        return Math.min(MAX_TRACK, BASE_TRACK + Math.ceil(Math.max(0, latest - BASE_TRACK) / 6) * 6);
    };
    const setTrack = hours => {
        if (hours === TRACK) return;
        TRACK = hours;
        const next = `${24 / TRACK * 100}%`;
        track.style.setProperty("--hm-next", next);
        track.style.setProperty("--hm-cols", String(TRACK / 3));
        if (nextDay) nextDay.style.left = next;
        const step = TRACK > BASE_TRACK ? 6 : 3;
        axis.replaceChildren(...Array.from({ length: TRACK / step + 1 }, (_, index) => {
            const hour = index * step;
            const label = document.createElement("span");
            label.textContent = String(hour % 24).padStart(2, "0");
            label.style.left = `${hour / TRACK * 100}%`;
            if (hour > 24) label.className = "hm-weditor__next";
            return label;
        }));
        for (const [item, block] of blocks) {
            const range = read(item);
            if (range) place(item, range);
            for (const handle of block.handles) handle.setAttribute("aria-valuemax", String(TRACK));
        }
    };

    function render() {
        setTrack(hoursNeeded());
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
        if (focusFields && item) field(item, "start").querySelector("select").focus({ preventScroll: true });
        // Only scroll vertically: an inline scroll here once moved the whole card sideways.
        if (item) track.scrollIntoView({ block: "nearest", inline: "nearest" });
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

// ---------------------------------------------------------------------------------------------------------------
// Shared listbox behaviour for the pickers below: arrow keys move, Enter picks, Escape closes.
const listbox = (list, onPick) => {
    let active = -1;
    const options = () => [...list.querySelectorAll("[role=option]:not([aria-disabled=true])")];
    const highlight = index => {
        const all = options();
        active = all.length === 0 ? -1 : (index + all.length) % all.length;
        all.forEach((option, position) => option.classList.toggle("is-active", position === active));
        all[active]?.scrollIntoView({ block: "nearest" });
        return all[active];
    };
    list.addEventListener("mousedown", event => event.preventDefault());
    list.addEventListener("click", event => {
        const option = event.target.closest("[role=option]");
        if (option && option.getAttribute("aria-disabled") !== "true") onPick(option);
    });
    return {
        reset: (index = -1) => highlight(index),
        get active() { return options()[active]; },
        keydown(event) {
            if (event.key === "ArrowDown") { event.preventDefault(); return highlight(active + 1), true; }
            if (event.key === "ArrowUp") { event.preventDefault(); return highlight(active - 1), true; }
            if (event.key === "Home" && event.ctrlKey) { event.preventDefault(); return highlight(0), true; }
            if (event.key === "End" && event.ctrlKey) { event.preventDefault(); return highlight(-1), true; }
            return false;
        }
    };
};
let pickerSerial = 0;

// ---------------------------------------------------------------------------------------------------------------
// Searchable dropdowns: <select data-picker> keeps posting its value; a button and a filterable list replace the
// native menu, which is long and hard to scan for servers with many channels or roles.
for (const select of document.querySelectorAll("select[data-picker]")) {
    const id = `picker-${pickerSerial++}`;
    const colors = JSON.parse(select.dataset.pickerColors || "{}");
    const wrap = document.createElement("div");
    wrap.className = "hm-picker";
    select.before(wrap);
    wrap.append(select);
    select.classList.add("hm-picker__native");
    select.tabIndex = -1;
    select.setAttribute("aria-hidden", "true");

    const button = document.createElement("button");
    button.type = "button";
    button.className = "hm-select hm-picker__button";
    button.id = `${select.id || id}-picker`;
    button.setAttribute("aria-haspopup", "listbox");
    button.setAttribute("aria-expanded", "false");
    for (const name of ["aria-invalid", "aria-describedby"])
        if (select.hasAttribute(name)) button.setAttribute(name, select.getAttribute(name));
    const label = select.id ? document.querySelector(`label[for="${select.id}"]`) : null;
    if (label) {
        label.htmlFor = button.id;
        label.id ||= `${button.id}-label`;
        // Name the button "label, current value" so screen readers announce what is selected.
        button.setAttribute("aria-labelledby", `${label.id} ${button.id}`);
    }

    const panel = document.createElement("div");
    panel.className = "hm-picker__panel";
    panel.hidden = true;
    const search = document.createElement("input");
    search.type = "search";
    search.className = "hm-input hm-picker__search";
    search.placeholder = select.dataset.pickerSearch || "";
    search.setAttribute("aria-label", select.dataset.pickerSearch || "");
    search.setAttribute("aria-controls", `${id}-list`);
    search.autocomplete = "off";
    const list = document.createElement("ul");
    list.className = "hm-picker__list";
    list.id = `${id}-list`;
    list.setAttribute("role", "listbox");
    if (label) list.setAttribute("aria-labelledby", label.id);
    const empty = document.createElement("p");
    empty.className = "hm-picker__empty";
    empty.textContent = select.dataset.pickerEmpty || "";
    empty.hidden = true;
    const searchable = select.options.length > 7;
    if (searchable) panel.append(search);
    panel.append(list, empty);
    wrap.append(button, panel);

    const decorate = (element, option) => {
        element.replaceChildren();
        const value = option.value;
        if (colors[value]) {
            const dot = document.createElement("span");
            dot.className = "hm-role-dot";
            dot.style.background = colors[value];
            element.append(dot);
        }
        const text = document.createElement("span");
        text.className = "hm-picker__text";
        text.textContent = option.text;
        element.append(text);
        element.classList.toggle("is-default", value === "" || value === "0");
    };
    const sync = () => decorate(button, select.options[select.selectedIndex] ?? select.options[0]);

    const build = filter => {
        const query = filter.trim().toLowerCase();
        list.replaceChildren();
        for (const option of select.options) {
            if (query && !option.text.toLowerCase().includes(query)) continue;
            const item = document.createElement("li");
            item.setAttribute("role", "option");
            item.className = "hm-picker__option";
            item.dataset.value = option.value;
            item.id = `${id}-${option.index}`;
            item.setAttribute("aria-selected", String(option.selected));
            decorate(item, option);
            item.insertAdjacentHTML("beforeend", '<svg class="hm-icon hm-picker__check" viewBox="0 0 24 24" aria-hidden="true"><path d="m5 12 5 5 9-10"/></svg>');
            list.append(item);
        }
        empty.hidden = list.children.length > 0;
        const selected = [...list.children].findIndex(item => item.getAttribute("aria-selected") === "true");
        nav.reset(selected >= 0 ? selected : 0);
    };

    const close = (focus = true) => {
        if (panel.hidden) return;
        panel.hidden = true;
        wrap.classList.remove("is-open");
        button.setAttribute("aria-expanded", "false");
        if (focus) button.focus();
    };
    const pick = item => {
        if (select.value !== item.dataset.value) {
            select.value = item.dataset.value;
            select.dispatchEvent(new Event("input", { bubbles: true }));
            select.dispatchEvent(new Event("change", { bubbles: true }));
            sync();
        }
        close();
    };
    const nav = listbox(list, pick);
    const open = () => {
        for (const other of document.querySelectorAll(".hm-picker.is-open")) other.dispatchEvent(new Event("picker-close"));
        search.value = "";
        build("");
        panel.hidden = false;
        wrap.classList.add("is-open");
        button.setAttribute("aria-expanded", "true");
        (searchable ? search : list).focus({ preventScroll: true });
        nav.active?.scrollIntoView({ block: "nearest" });
    };
    wrap.addEventListener("picker-close", () => close(false));
    list.tabIndex = -1;

    button.addEventListener("click", () => panel.hidden ? open() : close());
    button.addEventListener("keydown", event => {
        if (["ArrowDown", "ArrowUp", "Enter", " "].includes(event.key)) { event.preventDefault(); open(); }
    });
    const keys = event => {
        if (event.key === "Escape") { event.preventDefault(); event.stopPropagation(); close(); return; }
        if (event.key === "Tab") { close(false); return; }
        if (event.key === "Enter") { event.preventDefault(); if (nav.active) pick(nav.active); return; }
        nav.keydown(event);
    };
    search.addEventListener("keydown", keys);
    list.addEventListener("keydown", keys);
    // Typing in a search box changes nothing that is saved, so keep it from marking the form as edited.
    search.addEventListener("input", event => { event.stopPropagation(); build(search.value); });
    document.addEventListener("pointerdown", event => { if (!wrap.contains(event.target)) close(false); });
    select.addEventListener("change", sync);
    sync();
}

// ---------------------------------------------------------------------------------------------------------------
// Text boxes with suggestions (time zones): a filtered list instead of the browser's datalist, which shows hundreds
// of entries at once. Typing anything else still works.
for (const input of document.querySelectorAll("input[list]")) {
    const source = document.getElementById(input.getAttribute("list"));
    if (!source) continue;
    const values = [...source.options].map(option => option.value);
    const id = `combo-${pickerSerial++}`;
    input.removeAttribute("list");
    input.setAttribute("role", "combobox");
    input.setAttribute("aria-autocomplete", "list");
    input.setAttribute("aria-expanded", "false");
    input.setAttribute("aria-controls", `${id}-list`);
    const wrap = document.createElement("div");
    wrap.className = "hm-combo hm-combo--plain";
    input.before(wrap);
    wrap.append(input);
    const list = document.createElement("ul");
    list.className = "hm-combo__list";
    list.id = `${id}-list`;
    list.setAttribute("role", "listbox");
    list.hidden = true;
    wrap.append(list);

    const close = () => { list.hidden = true; input.setAttribute("aria-expanded", "false"); input.removeAttribute("aria-activedescendant"); };
    const pick = option => {
        input.value = option.dataset.value;
        input.dispatchEvent(new Event("input", { bubbles: true }));
        input.dispatchEvent(new Event("change", { bubbles: true }));
        close();
    };
    const nav = listbox(list, pick);
    const show = () => {
        const query = input.value.trim().toLowerCase().replaceAll(" ", "_");
        const matches = values.filter(value => !query || value.toLowerCase().includes(query))
            .sort((a, b) => Number(!a.toLowerCase().startsWith(query)) - Number(!b.toLowerCase().startsWith(query)))
            .slice(0, 40);
        list.replaceChildren(...matches.map((value, index) => {
            const item = document.createElement("li");
            item.setAttribute("role", "option");
            item.className = "hm-combo__option";
            item.id = `${id}-${index}`;
            item.dataset.value = value;
            const slash = value.indexOf("/");
            item.innerHTML = slash > 0 ? `<span class="hm-combo__region"></span><b></b>` : "<b></b>";
            if (slash > 0) item.firstChild.textContent = value.slice(0, slash + 1);
            item.querySelector("b").textContent = slash > 0 ? value.slice(slash + 1).replaceAll("_", " ") : value;
            return item;
        }));
        list.hidden = matches.length === 0;
        input.setAttribute("aria-expanded", String(!list.hidden));
        nav.reset(-1);
    };
    input.addEventListener("focus", show);
    input.addEventListener("input", event => { if (event.isTrusted) show(); });
    input.addEventListener("blur", close);
    input.addEventListener("keydown", event => {
        if (event.key === "Escape" && !list.hidden) { event.preventDefault(); event.stopPropagation(); close(); return; }
        if (event.key === "Enter" && !list.hidden && nav.active) { event.preventDefault(); pick(nav.active); return; }
        if (list.hidden && event.key === "ArrowDown") { show(); }
        if (nav.keydown(event)) input.setAttribute("aria-activedescendant", nav.active?.id ?? "");
    });
}

// ---------------------------------------------------------------------------------------------------------------
// Followed teams and Twitch channels: rows with a remove button and an add box over a one-per-line textarea.
for (const editor of document.querySelectorAll("[data-list-editor]")) {
    const kind = editor.dataset.kind;
    const max = Number(editor.dataset.max);
    const labels = editor.dataset;
    const textarea = editor.querySelector("textarea");
    const items = editor.querySelector("[data-list-items]");
    const emptyText = editor.querySelector("[data-list-empty]");
    const add = editor.querySelector("[data-list-add]");
    const input = editor.querySelector("[data-list-input]");
    const commit = editor.querySelector("[data-list-commit]");
    const options = editor.querySelector("[data-list-options]");
    const status = editor.querySelector("[data-list-status]");
    const textView = editor.querySelector("[data-list-text]");
    const foot = editor.querySelector("[data-list-foot]");
    const toggle = editor.querySelector("[data-list-toggle]");
    const count = editor.querySelector("[data-list-count]");
    const known = new Map(JSON.parse(labels.known || "[]").map(team => [team.id, team]));
    const format = (template, ...values) => values.reduce((text, value, index) => text.replace(`{${index}}`, value), template);
    let textMode = false;

    const lines = () => textarea.value.split("\n").map(line => line.trim()).filter(Boolean);
    const setLines = next => {
        textarea.value = next.join("\n");
        textarea.dispatchEvent(new Event("input", { bubbles: true }));
        render();
    };
    const idOf = line => { const match = /#\s*(\d+)\s*$/.exec(line) ?? /^(\d+)$/.exec(line); return match ? Number(match[1]) : null; };
    const handleOf = value => {
        let text = value.trim();
        const url = /^(?:https?:\/\/)?(?:www\.)?twitch\.tv\/([^/?#\s]+)\/?$/i.exec(text);
        if (url) text = url[1];
        return /^[A-Za-z0-9_]{1,25}$/.test(text) ? text.toLowerCase() : null;
    };
    const keyOf = line => kind === "teams" ? (idOf(line) !== null ? `#${idOf(line)}` : line.toLowerCase()) : (handleOf(line) ?? line.toLowerCase());
    const say = (text, tone = "") => { status.textContent = text; status.dataset.tone = tone; };

    const row = (line, index) => {
        const item = document.createElement("li");
        item.className = "hm-listedit__item";
        const mark = document.createElement("span");
        mark.className = "hm-listedit__mark";
        mark.setAttribute("aria-hidden", "true");
        const main = document.createElement("span");
        main.className = "hm-listedit__main";
        const title = document.createElement("span");
        title.className = "hm-listedit__title";
        const meta = document.createElement("span");
        meta.className = "hm-listedit__meta";
        let name = line;
        if (kind === "teams") {
            const id = idOf(line);
            const team = id !== null ? known.get(id) : null;
            name = team?.name ?? (line.replace(/#\s*\d+\s*$/, "").trim() || `#${id}`);
            if (team?.image) {
                const logo = document.createElement("img");
                logo.src = team.image; logo.alt = ""; logo.loading = "lazy"; logo.referrerPolicy = "no-referrer";
                logo.addEventListener("error", () => logo.remove());
                mark.append(logo);
            } else mark.textContent = (team?.acronym || name).replace(/[^\p{L}\p{N}]/gu, "").slice(0, 3).toUpperCase() || "?";
            meta.textContent = id === null ? labels.labelUnresolved
                : [team?.acronym && team.acronym !== name ? team.acronym : null, `#${id}`, team?.location].filter(Boolean).join(" · ");
            if (id === null) item.classList.add("is-pending");
        } else {
            const handle = handleOf(line);
            name = handle ?? line;
            mark.innerHTML = '<svg class="hm-icon" viewBox="0 0 24 24"><path d="M5 3.5 3.5 7v12.5h4.5V22l2.5-2.5h3.5L20.5 13V3.5zM11 8v5M15.5 8v5"/></svg>';
            meta.textContent = handle ? `twitch.tv/${handle}` : labels.labelInvalid;
            if (!handle) item.classList.add("is-invalid");
        }
        title.textContent = name;
        main.append(title, meta);
        const remove = document.createElement("button");
        remove.type = "button";
        remove.className = "hm-btn hm-btn--ghost hm-listedit__remove";
        remove.setAttribute("aria-label", format(labels.labelRemove, name));
        remove.innerHTML = '<svg class="hm-icon" viewBox="0 0 24 24" aria-hidden="true"><path d="M5 12h14"/></svg>';
        remove.addEventListener("click", () => {
            const next = lines();
            next.splice(index, 1);
            setLines(next);
            say(format(labels.labelRemoved, name));
            (items.children[Math.min(index, items.children.length - 1)]?.querySelector("button") ?? input).focus();
        });
        item.append(mark, main, remove);
        return item;
    };

    function render() {
        const current = lines();
        items.replaceChildren(...current.map(row));
        items.hidden = textMode || current.length === 0;
        emptyText.hidden = textMode || current.length > 0;
        add.hidden = textMode;
        textView.hidden = !textMode;
        const full = current.length >= max;
        input.disabled = full;
        commit.disabled = full;
        input.placeholder = full ? labels.labelFull : input.getAttribute("aria-label");
        count.textContent = format(labels.labelCount, current.length, max);
        count.classList.toggle("is-over", current.length > max);
    }

    const append = line => {
        const current = lines();
        if (current.length >= max) return say(labels.labelFull, "warn"), false;
        if (current.some(existing => keyOf(existing) === keyOf(line))) return say(labels.labelDuplicate, "warn"), false;
        setLines([...current, line]);
        say("");
        return true;
    };

    // Twitch: validate and add on Enter or the Add button.
    const addTyped = () => {
        const value = input.value.trim();
        if (!value) return;
        if (kind === "twitch") {
            const handle = handleOf(value);
            if (!handle) return say(labels.labelInvalid, "error");
            if (append(handle)) input.value = "";
            return;
        }
        if (searchNav?.active) return pickTeam(searchNav.active);
        if (append(value)) { input.value = ""; closeOptions(); }
    };

    // Teams: search as you type; pick a result, or press Enter to add the name as typed (resolved on save).
    let searchNav = null, timer = 0, request = null;
    const closeOptions = () => { options.hidden = true; input.setAttribute("aria-expanded", "false"); input.removeAttribute("aria-activedescendant"); };
    const pickTeam = option => {
        const team = JSON.parse(option.dataset.team);
        known.set(team.id, team);
        if (append(team.line)) input.value = "";
        closeOptions();
        input.focus();
    };
    if (kind === "teams") {
        searchNav = listbox(options, pickTeam);
        const search = async query => {
            request?.abort();
            request = new AbortController();
            say(labels.labelSearching);
            try {
                const response = await fetch(`${labels.searchUrl}&q=${encodeURIComponent(query)}`, { signal: request.signal, headers: { Accept: "application/json" } });
                if (!response.ok) throw new Error(String(response.status));
                const teams = await response.json();
                if (input.value.trim() !== query) return;
                const taken = new Set(lines().map(keyOf));
                options.replaceChildren(...teams.map((team, index) => {
                    const item = document.createElement("li");
                    item.setAttribute("role", "option");
                    item.className = "hm-combo__option hm-combo__team";
                    item.id = `${input.id}-option-${index}`;
                    item.dataset.team = JSON.stringify(team);
                    const already = taken.has(`#${team.id}`);
                    if (already) { item.setAttribute("aria-disabled", "true"); item.title = labels.labelDuplicate; }
                    const logo = team.image ? `<img src="" alt="" loading="lazy" referrerpolicy="no-referrer">` : `<span class="hm-combo__badge"></span>`;
                    item.innerHTML = `${logo}<span class="hm-combo__name"><b></b><small></small></span><span class="hm-combo__id"></span>`;
                    if (team.image) { const img = item.querySelector("img"); img.src = team.image; img.addEventListener("error", () => img.remove()); }
                    else item.querySelector(".hm-combo__badge").textContent = (team.acronym || team.name).slice(0, 3).toUpperCase();
                    item.querySelector("b").textContent = team.name;
                    item.querySelector("small").textContent = [team.acronym, team.location].filter(Boolean).join(" · ");
                    item.querySelector(".hm-combo__id").textContent = already ? "✓" : `#${team.id}`;
                    return item;
                }));
                options.hidden = teams.length === 0;
                input.setAttribute("aria-expanded", String(!options.hidden));
                searchNav.reset(teams.findIndex(team => !taken.has(`#${team.id}`)));
                say(teams.length === 0 ? labels.labelNone : "");
            } catch (error) {
                if (error.name === "AbortError") return;
                closeOptions();
                say(labels.labelUnavailable, "warn");
            }
        };
        input.addEventListener("input", () => {
            clearTimeout(timer);
            const query = input.value.trim();
            if (query.length < 2) { closeOptions(); say(""); return; }
            timer = setTimeout(() => search(query), 250);
        });
        input.addEventListener("blur", () => setTimeout(closeOptions, 120));
    }
    input.addEventListener("keydown", event => {
        if (event.key === "Enter") { event.preventDefault(); addTyped(); return; }
        if (event.key === "Escape" && searchNav && !options.hidden) { event.preventDefault(); event.stopPropagation(); closeOptions(); return; }
        if (searchNav && !options.hidden && searchNav.keydown(event)) input.setAttribute("aria-activedescendant", searchNav.active?.id ?? "");
    });
    commit.addEventListener("click", addTyped);
    input.addEventListener("input", event => event.stopPropagation());

    toggle.addEventListener("click", () => {
        textMode = !textMode;
        toggle.setAttribute("aria-pressed", String(textMode));
        toggle.querySelector("span").textContent = textMode ? labels.labelList : labels.labelText;
        render();
        (textMode ? textarea : input).focus();
    });
    textarea.addEventListener("input", event => { if (event.isTrusted) render(); });

    // Switching a list to Custom starts from what it inherited, so one team can be removed without retyping the rest.
    const field = editor.closest(".hm-mode-field");
    field?.addEventListener("change", event => {
        if (event.target.matches("input[type=radio][value=custom]") && lines().length === 0 && labels.defaults) {
            textarea.value = labels.defaults;
            render();
        }
    });

    foot.hidden = false;
    render();
}

// ---------------------------------------------------------------------------------------------------------------
// New event: spell the schedule out in plain language before anything is created, filter the member list, and
// show how many people the submit button will invite. The form works without this; the server checks everything.
for (const form of document.querySelectorAll("[data-event-form]")) {
    const labels = form.dataset;
    const box = form.querySelector("[data-event-summary-box]");
    const summary = form.querySelector("[data-event-summary]");
    const detail = form.querySelector("[data-event-summary-detail]");
    const submit = form.querySelector("[data-event-submit]");
    const value = name => form.querySelector(`[name="${name}"]:checked`)?.value ?? form.querySelector(`[name="${name}"]`)?.value ?? "";
    const format = (template, ...values) => values.reduce((text, item, index) => text.replaceAll(`{${index}}`, item), template);
    const join = items => items.length < 2 ? (items[0] ?? "") : format(labels.labelAnd, items.slice(0, -1).join(", "), items.at(-1));
    const parse = text => /^\d{4}-\d{2}-\d{2}$/.test(text) ? new Date(`${text}T12:00:00`) : null;
    const locale = document.documentElement.lang || undefined;
    const filter = form.querySelector("[data-people-filter]");
    const people = [...form.querySelectorAll("[data-person]")];
    const peopleCount = form.querySelector("[data-people-count]");

    const update = () => {
        const repeat = value("Repeat");
        const start = parse(value("StartDate"));
        const until = parse(value("Until"));
        const from = `${value("StartHour")}:${value("StartMinute")}`, to = `${value("EndHour")}:${value("EndMinute")}`;
        const times = `${from}–${to}${to <= from ? ` (${labels.labelNextDay})` : ""}`;
        const zone = form.querySelector("[data-event-zone]")?.value.trim() || form.querySelector("[data-event-zone]")?.placeholder || "";
        const days = [...form.querySelectorAll("[name=Weekdays]:checked")];
        let text = "", note = "";
        if (start) {
            const longDate = start.toLocaleDateString(locale, { weekday: "long", day: "numeric", month: "long", year: "numeric" });
            const untilText = until?.toLocaleDateString(locale, { day: "numeric", month: "short", year: "numeric" }) ?? "…";
            if (repeat === "once") text = format(labels.labelOnce, longDate, times);
            else if (repeat === "daily") text = format(labels.labelDaily, times, untilText);
            else text = format(labels.labelWeekly, days.length ? join(days.map(day => day.dataset.dayName)) : "…", times, untilText);
            if (repeat !== "once") {
                if (repeat === "weekly" && days.length === 0) note = labels.labelPickDays;
                else if (!until || until < start) note = labels.labelPickUntil;
                else {
                    const wanted = new Set(days.map(day => day.value));
                    const names = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
                    let count = 0;
                    for (const day = new Date(start); day <= until; day.setDate(day.getDate() + 1))
                        if (repeat === "daily" || wanted.has(names[day.getDay()])) count++;
                    note = format(labels.labelDates, count);
                }
            }
            text += ` · ${zone}`;
        }
        box.hidden = !text;
        summary.textContent = text;
        detail.textContent = note;
        detail.hidden = !note;

        const audience = value("Audience");
        const invited = audience === "group"
            ? Number(form.querySelector("select[name=GroupId]")?.selectedOptions[0]?.dataset.count ?? 0)
            : form.querySelectorAll("[name=Invitees]:checked").length;
        if (peopleCount) peopleCount.textContent = audience === "people" ? `${form.querySelectorAll("[name=Invitees]:checked").length}` : "";
        const label = invited > 0 ? format(labels.labelInvite, invited) : labels.labelCreate;
        submit.lastChild.textContent = label;
    };

    if (filter) {
        filter.hidden = people.length < 8;
        filter.addEventListener("input", event => {
            event.stopPropagation();
            const query = filter.value.trim().toLowerCase();
            for (const person of people) person.hidden = !!query && !person.dataset.person.includes(query);
        });
    }
    form.addEventListener("input", update);
    form.addEventListener("change", update);
    update();
}

for (const image of document.querySelectorAll("[data-game-art]")) {
    image.addEventListener("error", () => { image.hidden = true; });
}

// ---------------------------------------------------------------------------------------------------------------
// Availability copying: drag a weekly day onto another day, or a marked calendar date onto another date. The day you
// drop on is replaced; a dialog says so first. "Copy to" menus also get quick picks.
(() => {
    const dialog = document.querySelector("[data-drop-dialog]");
    for (const form of document.querySelectorAll("[data-drop-form]")) {
        const scope = form.closest(".hm-card") ?? document;
        const targets = [...scope.querySelectorAll("[data-drop-day], [data-drop-date]")];
        if (!targets.length) continue;
        const key = target => target.dataset.dropDay ?? target.dataset.dropDate;
        scope.querySelectorAll("[data-drag-tip]").forEach(tip => { tip.hidden = false; });
        let source = null;
        const clear = () => targets.forEach(target => target.classList.remove("is-drop-target", "is-dragging"));
        const submit = (from, to) => {
            form.querySelector("[data-drop-from]").value = key(from);
            form.querySelectorAll("[data-drop-to]").forEach(input => { input.value = key(to); });
            if (!dialog?.showModal) { form.submit(); return; }
            const replace = to.dataset.hasHours === "true";
            const mode = replace ? "replace" : "copy";
            dialog.querySelector("[data-drop-message]").textContent = dialog.dataset[mode]
                .replaceAll("{0}", from.dataset.dayName).replaceAll("{1}", to.dataset.dayName);
            const ok = dialog.querySelector("[data-drop-ok]");
            const label = ok.querySelector("[data-drop-ok-label]");
            label.textContent = label.dataset[mode];
            ok.classList.toggle("hm-btn--danger", replace);
            ok.classList.toggle("hm-btn--primary", !replace);
            dialog.returnValue = "";
            dialog.onclose = () => { if (dialog.returnValue === "ok") form.submit(); };
            dialog.showModal();
        };
        for (const target of targets) {
            target.addEventListener("dragstart", event => {
                if (target.getAttribute("draggable") !== "true") { event.preventDefault(); return; }
                source = target;
                target.classList.add("is-dragging");
                event.dataTransfer.effectAllowed = "copy";
                event.dataTransfer.setData("text/plain", key(target));
            });
            target.addEventListener("dragend", () => { source = null; clear(); });
            target.addEventListener("dragover", event => {
                if (!source || source === target) return;
                event.preventDefault();
                event.dataTransfer.dropEffect = "copy";
                target.classList.add("is-drop-target");
            });
            target.addEventListener("dragleave", event => {
                if (!target.contains(event.relatedTarget)) target.classList.remove("is-drop-target");
            });
            target.addEventListener("drop", event => {
                event.preventDefault();
                const from = source;
                clear();
                if (from && from !== target) submit(from, target);
            });
        }
    }

    for (const menu of document.querySelectorAll("[data-copy-to]")) {
        const quick = menu.querySelector("[data-copy-quick]");
        quick.hidden = false;
        const boxes = [...menu.querySelectorAll('input[name="copyTo"]')];
        const submit = menu.querySelector('button[value="copy-weekly"]');
        const sync = () => { submit.disabled = !boxes.some(box => box.checked); };
        quick.addEventListener("click", event => {
            const pick = event.target.closest("[data-pick]")?.dataset.pick;
            if (!pick) return;
            for (const box of boxes)
                box.checked = pick === "all" || (pick === "weekend") === (box.dataset.weekend === "true");
            sync();
        });
        menu.addEventListener("change", sync);
        sync();
    }

    // "Repeat through" is only posted while its checkbox is ticked.
    for (const toggle of document.querySelectorAll("[data-repeat-toggle]")) {
        const input = toggle.form?.querySelector('[name="through"]');
        if (!input) continue;
        const sync = () => { input.disabled = !toggle.checked; };
        toggle.addEventListener("change", () => { sync(); if (toggle.checked) input.focus(); });
        sync();
    }

    // Game news: filter the cards by name.
    const search = document.querySelector("[data-news-search]");
    if (search) {
        search.hidden = false;
        const cards = [...document.querySelectorAll("[data-news-name]")];
        search.querySelector("input").addEventListener("input", event => {
            const query = event.target.value.trim().toLowerCase();
            for (const card of cards) card.hidden = query !== "" && !card.dataset.newsName.includes(query);
            const none = document.querySelector("[data-news-none]");
            if (none) none.hidden = cards.some(card => !card.hidden);
        });
    }
})();

// Pop-over panels ("Copy to", confirmations): open upwards when there's no room below, keep one open at a time, and
// close when a click or keyboard focus moves somewhere else on the page.
(() => {
    const panels = () => document.querySelectorAll("details.hm-copyto[open], details.hm-confirm:not(.hm-confirm--inline)[open]");
    const closeOthers = keep => { for (const details of panels()) if (details !== keep) details.open = false; };
    for (const details of document.querySelectorAll("details.hm-copyto, details.hm-confirm:not(.hm-confirm--inline)")) {
        details.addEventListener("toggle", () => {
            if (!details.open) return;
            closeOthers(details);
            const panel = details.querySelector(".hm-copyto__panel, .hm-confirm__panel");
            const up = details.classList.contains("hm-copyto") ? "hm-copyto--up" : "hm-confirm--up";
            details.classList.remove(up);
            if (!panel || getComputedStyle(panel).position !== "absolute") return;
            const below = window.innerHeight - details.getBoundingClientRect().bottom;
            if (panel.offsetHeight + 16 > below && details.getBoundingClientRect().top > panel.offsetHeight + 16)
                details.classList.add(up);
        });
    }
    const outside = event => { for (const details of panels()) if (!details.contains(event.target)) details.open = false; };
    document.addEventListener("pointerdown", outside);
    document.addEventListener("focusin", outside);
})();
