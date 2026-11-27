// Phasmophobia: the ghost database search, the ghost entries and the evidence finder.
// The rules (which evidence a contract can show for each ghost, forced evidence, The Mimic's extra orbs) are worked
// out on the server by EvidenceFinder.ShownSets and sent with each ghost as data-sets; this file only checks the
// marks against those sets, the same check EvidenceFinder.Evaluate makes.

/**
 * One ghost against the marks.
 * @param {{evidence: string[], forced: string[], extra: string[], sets: Record<string, {on: string[][], off: string[][]}>}} ghost
 * @param {Record<string, "unknown"|"confirmed"|"ruledout">} marks
 * @param {number} count evidence the contract gives, 0–3
 * @param {boolean} forced whether forced evidence is kept on reduced-evidence contracts
 */
function phasmoEvaluate(ghost, marks, count, forced) {
    const sets = ghost.sets[String(count)][forced ? "on" : "off"];
    const ids = Object.keys(marks);
    const confirmed = ids.filter(id => marks[id] === "confirmed");
    const ruledOut = ids.filter(id => marks[id] === "ruledout");
    const fitting = sets.filter(set => confirmed.every(id => set.includes(id)) && !ruledOut.some(id => set.includes(id)));
    const all = [...ghost.evidence, ...ghost.extra];
    const explained = confirmed.filter(id => all.includes(id));
    const possible = fitting.length > 0;
    const stillToFind = possible
        ? ghost.evidence.filter(id => (marks[id] ?? "unknown") === "unknown" && fitting.some(set => set.includes(id)))
        : [];
    let reason = null;
    if (!possible) {
        const lacking = confirmed.find(id => !all.includes(id));
        const always = ruledOut.find(id => sets.every(set => set.includes(id)));
        reason = lacking ? { code: "lacks", evidence: lacking }
            : always ? { code: forced && count > 0 && count < 3 && ghost.forced.includes(always) ? "forced" : "has", evidence: always }
            : { code: "count" };
    }
    return { possible, confirmed: explained, stillToFind, matches: explained.length, reason };
}

if (typeof module !== "undefined") module.exports = { phasmoEvaluate };

if (typeof document !== "undefined") {
    // Ghost entries: Details (and a ghost's name in the finder) opens its dialog; #ghost-<id> in the address opens it too.
    (() => {
        const open = id => {
            const dialog = document.getElementById("ghost-" + id);
            if (!dialog?.showModal || dialog.open) return;
            dialog.showModal();
        };
        document.addEventListener("click", event => {
            const button = event.target.closest?.("[data-ghost-open]");
            if (button) open(button.dataset.ghostOpen);
        });
        for (const dialog of document.querySelectorAll("[data-ghost-dialog]")) {
            // A click on the backdrop closes the entry.
            dialog.addEventListener("click", event => { if (event.target === dialog) dialog.close(); });
        }
        if (location.hash.startsWith("#ghost-")) open(decodeURIComponent(location.hash.slice(7)));
    })();

    const matchesSearch = (item, query) => query === "" || item.dataset.ghostSearch.includes(query);

    // Ghost database: the search hides cards as you type.
    (() => {
        const box = document.querySelector("[data-phasmo-db]");
        if (!box) return;
        const search = box.querySelector("[data-phasmo-db-search]");
        const cards = [...box.querySelectorAll("[data-phasmo-db-list] > li")];
        const count = box.querySelector("[data-phasmo-db-count]");
        const empty = box.querySelector("[data-phasmo-db-empty]");
        search.addEventListener("input", () => {
            const query = search.value.trim().toLowerCase();
            let shown = 0;
            for (const card of cards) {
                card.hidden = !matchesSearch(card, query);
                if (!card.hidden) shown++;
            }
            count.textContent = shown;
            empty.hidden = shown > 0;
        });
    })();

    // Evidence finder.
    (() => {
        const root = document.querySelector("[data-phasmo-finder]");
        if (!root) return;
        root.hidden = false;
        const t = root.dataset;
        const names = JSON.parse(t.names);
        const format = (template, value) => template.replace("{0}", value);
        const nameList = ids => ids.map(id => names[id]).join(", ");
        const collator = new Intl.Collator(document.documentElement.lang || undefined, { sensitivity: "base" });

        const buttons = [...root.querySelectorAll("[data-evidence]")];
        const difficulty = root.querySelector("[data-phasmo-difficulty]");
        const custom = root.querySelector("[data-phasmo-custom]");
        const customCount = root.querySelector("[data-phasmo-count]");
        const forcedRule = root.querySelector("[data-phasmo-forced]");
        const search = root.querySelector("[data-phasmo-search]");
        const sort = root.querySelector("[data-phasmo-sort]");
        const possibleList = root.querySelector("[data-phasmo-possible]");
        const outList = root.querySelector("[data-phasmo-out]");
        const outBox = root.querySelector("[data-phasmo-out-box]");
        const states = { unknown: t.tUnknown, confirmed: t.tConfirmed, ruledout: t.tRuledout };
        const next = { unknown: "confirmed", confirmed: "ruledout", ruledout: "unknown" };

        const items = [...possibleList.children].map(item => ({
            item,
            name: item.dataset.name,
            distinctive: item.hasAttribute("data-distinctive"),
            ghost: {
                evidence: item.dataset.evidence.split(" ").filter(Boolean),
                forced: item.dataset.forced.split(" ").filter(Boolean),
                extra: item.dataset.extra.split(" ").filter(Boolean),
                sets: JSON.parse(item.dataset.sets)
            }
        }));
        let marks = {};

        const evidenceCount = () => {
            const option = difficulty.selectedOptions[0];
            return option.value === "custom" ? Number(customCount.value) : Number(option.dataset.count);
        };

        const reasonText = reason => reason.code === "count"
            ? format(t.tCount, evidenceCount())
            : format(t[{ lacks: "tLacks", forced: "tForced", has: "tHas" }[reason.code]], names[reason.evidence]);

        const render = () => {
            const count = evidenceCount();
            const forced = forcedRule.checked;
            const query = search.value.trim().toLowerCase();
            custom.hidden = difficulty.value !== "custom";
            root.querySelector("[data-phasmo-zero]").hidden = count !== 0;
            const results = items.map(entry => ({ ...entry, result: phasmoEvaluate(entry.ghost, marks, count, forced) }));
            results.sort((a, b) => (sort.value === "matches" ? b.result.matches - a.result.matches : 0) || collator.compare(a.name, b.name));
            let left = 0, out = 0;
            for (const { item, result, distinctive, ghost } of results) {
                for (const chip of item.querySelectorAll("[data-ev]")) {
                    const id = chip.dataset.ev;
                    const mark = marks[id] ?? "unknown";
                    chip.dataset.mark = mark !== "unknown" ? mark
                        : result.possible && ghost.evidence.includes(id) && !result.stillToFind.includes(id) ? "hidden" : "";
                }
                const status = item.querySelector("[data-result-status]");
                status.textContent = !result.possible ? reasonText(result.reason)
                    : result.stillToFind.length > 0 ? format(t.tMissing, nameList(result.stillToFind))
                    : count === 0 ? t.tNothing : t.tComplete;
                const matches = item.querySelector("[data-result-matches]");
                matches.hidden = result.matches === 0;
                matches.textContent = format(t.tMatches, result.matches);
                item.classList.toggle("is-standout", count === 0 && distinctive);
                item.hidden = !matchesSearch(item, query);
                (result.possible ? possibleList : outList).append(item);
                if (result.possible) left++; else out++;
            }
            root.querySelector("[data-phasmo-left]").textContent = left;
            root.querySelector("[data-phasmo-none]").hidden = left > 0;
            root.querySelector("[data-phasmo-out-count]").textContent = out;
            outBox.hidden = out === 0;
        };

        const show = button => {
            const state = marks[button.dataset.evidence] ?? "unknown";
            button.dataset.state = state;
            button.querySelector("[data-state-label]").textContent = states[state];
            button.setAttribute("aria-label", button.querySelector(".hm-evbtn__name").textContent + ": " + states[state]);
        };
        for (const button of buttons) {
            show(button);
            button.addEventListener("click", () => {
                marks = { ...marks, [button.dataset.evidence]: next[marks[button.dataset.evidence] ?? "unknown"] };
                show(button);
                render();
            });
        }
        root.querySelector("[data-phasmo-reset]").addEventListener("click", () => {
            marks = {};
            buttons.forEach(show);
            render();
        });
        // The search only hides results; it never changes the marks or the difficulty.
        for (const control of [difficulty, customCount, forcedRule, sort]) control.addEventListener("change", render);
        search.addEventListener("input", render);
        render();
    })();
}
