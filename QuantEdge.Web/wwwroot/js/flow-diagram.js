/**
 * Shared SVG flow-diagram renderer (window.QeFlow), used by the admin Real Trade Flow screen
 * (tradelogic.js) and the per-stock journey popup on the Auto Real Trade page (realtrade-journey.js).
 *
 * A diagram is { nodes, edges }: nodes sit on a column/row grid (or free x/y/w/h overrides), edges
 * join node sides ("l" | "r" | "t" | "b") with orthogonal routes. Styling lives in flow-diagram.css;
 * render inside an element with the "qe-flow-canvas" class.
 */
window.QeFlow = (function () {
    "use strict";

    const PX = 200, PY = 90, OX = 25, OY = 30, W = 150, H = 50;

    const esc = s => String(s).replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;").replace(/"/g, "&quot;");

    // Node on the grid. o overrides x/y/w/h, and may set money: true (first sub line shown as an amount).
    const node = (id, col, row, kind, title, sub, info, value, src, o) =>
        Object.assign({ id, x: OX + col * PX, y: OY + row * PY, w: W, h: H, kind, title, sub, info, value, src }, o || {});

    // Edge from node side fs to node side ts. o: fo/to = port offsets, lp: "b" = label below, c: "r" = red.
    const edge = (f, t, fs, ts, label, o) => Object.assign({ f, t, fs, ts, label }, o || {});

    const KIND = {
        start: { fill: "#1e293b", stroke: "#a78bfa", text: "#f1f5f9", icon: "▶" },
        proc: { fill: "#1e293b", stroke: "#34425f", text: "#f1f5f9", icon: "•" },
        dec: { fill: "#172033", stroke: "#7c8db5", text: "#e2e8f0", icon: "?" },
        ok: { fill: "#10302a", stroke: "#34d399", text: "#34d399", icon: "✓" },
        bad: { fill: "#3a1d24", stroke: "#f87171", text: "#f87171", icon: "✕" },
        warn: { fill: "#33290f", stroke: "#fbbf24", text: "#fbbf24", icon: "…" },
        info: { fill: "#132a4a", stroke: "#4f9cf9", text: "#4f9cf9", icon: "i" },
        end: { fill: "#1e293b", stroke: "#94a3b8", text: "#f1f5f9", icon: "■" },
        ext: { fill: "#0d1221", stroke: "#94a3b8", text: "#cbd5e1", icon: "Z", dashed: true },
        // Journey states: a step that already happened, where the stock is now, and possible futures.
        done: { fill: "#12261f", stroke: "#2f8f6d", text: "#e2e8f0", icon: "✓" },
        now: { fill: "#1e1b3a", stroke: "#a78bfa", text: "#f1f5f9", icon: "●" },
        win: { fill: "#10302a", stroke: "#34d399", text: "#34d399", icon: "₹", dashed: true },
        loss: { fill: "#3a1d24", stroke: "#f87171", text: "#f87171", icon: "₹", dashed: true },
        later: { fill: "#33290f", stroke: "#fbbf24", text: "#fbbf24", icon: "…", dashed: true },
        skip: { fill: "#141b2d", stroke: "#34425f", text: "#64748b", icon: "–", dashed: true }
    };

    const MARKER = { gray: "qf-ahg", red: "qf-ahr", active: "qf-ahp", done: "qf-ahd" };
    const MARKERS_SVG = [[MARKER.gray, "#64748b", ""], [MARKER.red, "#f87171", ""], [MARKER.active, "#a78bfa", "qf-active-fill"], [MARKER.done, "#2f8f6d", ""]]
        .map(([id, color, cls]) => `<marker id="${id}" viewBox="0 0 10 10" refX="9" refY="5" markerWidth="7" markerHeight="7" orient="auto-start-reverse"><path d="M0,0 L10,5 L0,10 z" fill="${color}"${cls ? ` class="${cls}"` : ""}/></marker>`)
        .join("");

    const isHorizontal = side => side === "l" || side === "r";

    function port(n, side, offset) {
        const o = offset || 0;
        switch (side) {
            case "l": return { x: n.x, y: n.y + n.h / 2 + o };
            case "r": return { x: n.x + n.w, y: n.y + n.h / 2 + o };
            case "t": return { x: n.x + n.w / 2 + o, y: n.y };
            default: return { x: n.x + n.w / 2 + o, y: n.y + n.h };
        }
    }

    // Orthogonal route between two ports; returns the path and where its label sits.
    function route(e, nodes) {
        const p = port(nodes[e.f], e.fs, e.fo), q = port(nodes[e.t], e.ts, e.to);
        let d, lx, ly, anchor = "middle";
        if (isHorizontal(e.fs) && isHorizontal(e.ts)) {
            if (Math.abs(p.y - q.y) < 1) { d = `M${p.x},${p.y}H${q.x}`; lx = (p.x + q.x) / 2; ly = p.y - 10; }
            else { const mx = (p.x + q.x) / 2; d = `M${p.x},${p.y}H${mx}V${q.y}H${q.x}`; lx = mx; ly = (p.y + q.y) / 2; }
        } else if (!isHorizontal(e.fs) && !isHorizontal(e.ts)) {
            if (Math.abs(p.x - q.x) < 1) { d = `M${p.x},${p.y}V${q.y}`; lx = p.x + 8; ly = (p.y + q.y) / 2; anchor = "start"; }
            else { const my = (p.y + q.y) / 2; d = `M${p.x},${p.y}V${my}H${q.x}V${q.y}`; lx = (p.x + q.x) / 2; ly = my - 10; }
        } else if (isHorizontal(e.fs)) {
            d = `M${p.x},${p.y}H${q.x}V${q.y}`; lx = (p.x + q.x) / 2; ly = p.y - 10;
        } else {
            d = `M${p.x},${p.y}V${q.y}H${q.x}`; lx = p.x + 8; ly = (p.y + q.y) / 2; anchor = "start";
        }
        if (e.lp === "b") ly += 20;
        return { d, lx, ly, anchor };
    }

    // state: "" | "done" (already happened) | "cur" (the rule path being taken right now)
    function edgeSvg(e, nodes, state) {
        const r = route(e, nodes);
        const red = e.c === "r", done = state === "done", cur = state === "cur";
        const marker = cur ? MARKER.active : done ? MARKER.done : red ? MARKER.red : MARKER.gray;
        const stroke = done ? "#2f8f6d" : red ? "#f87171" : "#64748b";
        let label = "";
        if (e.label) {
            const w = e.label.length * 6 + 10;
            const rx = r.anchor === "start" ? r.lx - 4 : r.lx - w / 2;
            const color = cur ? "#c4b5fd" : red ? "#fca5a5" : "#94a3b8";
            label = `<rect x="${rx}" y="${r.ly - 9}" width="${w}" height="17" rx="4" fill="#172033"/>` +
                `<text x="${r.anchor === "start" ? r.lx + 1 : r.lx}" y="${r.ly + 3.5}" text-anchor="${r.anchor}" font-size="10.5" font-weight="500" fill="${color}">${esc(e.label)}</text>`;
        }
        return `<g class="e${cur ? " cur" : ""}" data-k="${e.f}>${e.t}"><path d="${r.d}" fill="none" stroke="${stroke}" stroke-width="${done ? 2 : 1.6}" stroke-opacity="${red && !cur ? 0.55 : 1}" marker-end="url(#${marker})" data-mk="${marker}"/>${label}</g>`;
    }

    function nodeSvg(n, nowId) {
        const k = KIND[n.kind] || KIND.proc, { x, y, w, h } = n;
        const shape = n.kind === "dec"
            ? `<polygon class="shape" points="${x + 14},${y} ${x + w - 14},${y} ${x + w},${y + h / 2} ${x + w - 14},${y + h} ${x + 14},${y + h} ${x},${y + h / 2}" fill="${k.fill}" stroke="${k.stroke}" stroke-width="1.5"/>`
            : `<rect class="shape" x="${x}" y="${y}" width="${w}" height="${h}" rx="10" fill="${k.fill}" stroke="${k.stroke}" stroke-width="${n.kind === "now" ? 2.2 : 1.5}"${k.dashed ? ' stroke-dasharray="5 4"' : ""}/>`;
        const titles = String(n.title).split("\n"), subs = n.sub ? String(n.sub).split("\n") : [];
        const top = y + h / 2 - (titles.length * 15 + subs.length * 13) / 2, cx = x + w / 2;
        let text = titles.map((t, i) =>
            `<text x="${cx}" y="${top + 11 + i * 15}" text-anchor="middle" font-size="13" font-weight="600" fill="${k.text}">${esc(t)}</text>`).join("");
        text += subs.map((s, j) => {
            const isAmount = n.money && j === 0;
            return `<text x="${cx}" y="${top + titles.length * 15 + 10 + j * 13}" text-anchor="middle" font-size="10.5"` +
                ` font-weight="${isAmount ? 700 : 500}" fill="${isAmount ? k.text : "#94a3b8"}"${isAmount ? ' class="qf-amount"' : ""}>${esc(s)}</text>`;
        }).join("");
        const badge = n.kind === "done"
            ? `<circle cx="${x + w - 2}" cy="${y + 2}" r="8" fill="#10302a" stroke="#34d399"/><text x="${x + w - 2}" y="${y + 5.5}" text-anchor="middle" font-size="10" font-weight="700" fill="#34d399">✓</text>`
            : "";
        const cls = "n" + (n.id === nowId ? " now" : "");
        return `<g class="${cls}" data-id="${esc(n.id)}" tabindex="0" role="button" aria-label="${esc(String(n.title).replace(/\n/g, " "))}">${shape}${text}${badge}</g>`;
    }

    /**
     * Draws a diagram into an <svg>. opts.edgeState(edge) returns "", "done" or "cur";
     * opts.nowId marks the current node; opts.extraSvg is drawn beneath the diagram.
     * Returns a map of node id -> node.
     */
    function render(svg, diagram, opts) {
        const o = opts || {};
        const nodes = {};
        diagram.nodes.forEach(n => { nodes[n.id] = n; });
        const height = Math.max(...diagram.nodes.map(n => n.y + n.h)) + 24;
        svg.setAttribute("viewBox", `0 0 1000 ${height}`);
        svg.classList.remove("dim");
        svg.innerHTML = `<defs>${MARKERS_SVG}</defs><g font-family="Inter, system-ui, sans-serif">` +
            (o.extraSvg || "") +
            diagram.edges.map(e => edgeSvg(e, nodes, o.edgeState ? o.edgeState(e) : "")).join("") +
            diagram.nodes.map(n => nodeSvg(n, o.nowId)).join("") + `</g>`;
        return nodes;
    }

    /** Calls onSelect(id) when a node is clicked or activated from the keyboard. */
    function onNodeSelect(svg, onSelect) {
        svg.querySelectorAll(".n").forEach(g => {
            g.addEventListener("click", () => onSelect(g.dataset.id));
            g.addEventListener("keydown", ev => {
                if (ev.key === "Enter" || ev.key === " ") { ev.preventDefault(); onSelect(g.dataset.id); }
            });
        });
    }

    function markSelected(svg, id) {
        svg.querySelectorAll(".n").forEach(g => g.classList.toggle("sel", g.dataset.id === id));
    }

    return { GRID: { PX, PY, OX, OY, W, H }, KIND, MARKER, esc, node, edge, render, onNodeSelect, markSelected };
})();
