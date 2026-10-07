/**
 * Reconciliation (Views/Reconciliation/Index).
 *
 *   GET /api/reconciliation/positions -> open real positions vs Zerodha (2 Kite calls, cached 60 s server-side)
 *   GET /api/reconciliation/issues    -> data-quality issues from the daily checks (DB only)
 *
 * The position check runs only when "Compare now" is clicked, never on a timer, so the page can't burn Kite calls.
 */
(function () {
    "use strict";

    const root = document.getElementById("reconciliationPage");
    if (!root) return;
    const cfg = window.QuantEdgeConfig || {};
    const apiBaseUrl = root.dataset.api || cfg.apiBaseUrl || "";
    const userId = Number(root.dataset.user || cfg.userId || 1);

    const el = id => document.getElementById(id);
    const esc = s => String(s == null ? "" : s).replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
    const money = v => (v < 0 ? "-" : "") + "₹" + Math.abs(Number(v || 0)).toLocaleString("en-IN", { minimumFractionDigits: 2, maximumFractionDigits: 2 });
    const signed = v => (v > 0 ? "+" : "") + money(v);
    const num = v => v == null ? "–" : Number(v).toLocaleString("en-IN", { maximumFractionDigits: 4 });
    const timeIst = utc => new Date(utc).toLocaleString("en-IN", { day: "2-digit", month: "short", hour: "2-digit", minute: "2-digit", timeZone: "Asia/Kolkata" });

    const STATUS = {
        MATCH: { cls: "rc-ok", label: "Match" },
        QUANTITY: { cls: "rc-bad", label: "Quantity differs" },
        AVERAGE_PRICE: { cls: "rc-warn", label: "Avg price differs" },
        MISSING_AT_ZERODHA: { cls: "rc-bad", label: "Not at Zerodha" }
    };

    const CHECKS = {
        DAILY_CLOSE_VS_BHAVCOPY: "Daily close vs NSE",
        POSITION_QTY_VS_ZERODHA: "Position qty vs Zerodha",
        POSITION_AVG_VS_ZERODHA: "Position avg vs Zerodha",
        POSITION_MISSING_AT_ZERODHA: "Position not at Zerodha"
    };

    async function comparePositions() {
        const btn = el("rcCompare");
        btn.disabled = true;
        el("rcPositions").innerHTML = `<tr><td colspan="10" class="rc-empty">Checking with Zerodha…</td></tr>`;
        try {
            const res = await fetch(`${apiBaseUrl}/api/reconciliation/positions?userId=${userId}`);
            if (!res.ok) throw new Error(await res.text());
            const data = await res.json();
            el("rcCheckedAt").textContent = data.checkedAtUtc ? `Checked ${timeIst(data.checkedAtUtc)} IST` : "";
            if (!data.success) {
                el("rcPositions").innerHTML = `<tr><td colspan="10" class="rc-empty">${esc(data.message || "Zerodha could not be reached.")}</td></tr>`;
                return;
            }
            const rows = data.rows || [];
            if (rows.length === 0) {
                el("rcPositions").innerHTML = `<tr><td colspan="10" class="rc-empty">${esc(data.message || "No open real positions.")}</td></tr>`;
                return;
            }
            el("rcPositions").innerHTML = rows.map(r => {
                const st = STATUS[r.status] || STATUS.MATCH;
                const qtyBad = r.status === "QUANTITY" || r.status === "MISSING_AT_ZERODHA";
                const avgBad = r.status === "AVERAGE_PRICE";
                return `<tr>
                    <td><strong>${esc(r.symbol)}</strong></td>
                    <td><span class="rc-badge ${st.cls}">${st.label}</span></td>
                    <td class="num ${qtyBad ? "rc-cell-bad" : ""}">${r.ourQuantity} / ${r.brokerQuantity}</td>
                    <td class="num ${avgBad ? "rc-cell-bad" : ""}">${money(r.ourAveragePrice)} / ${money(r.brokerAveragePrice)}</td>
                    <td class="num">${r.lastPrice ? money(r.lastPrice) : "–"}</td>
                    <td class="num">${signed(r.ourGrossPnl)}</td>
                    <td class="num">${signed(r.brokerPnl)}</td>
                    <td class="num">-${money(r.estimatedCharges)}</td>
                    <td class="num">${signed(r.ourNetPnl)}</td>
                    <td>${esc(r.explanation)}</td>
                </tr>`;
            }).join("");
        } catch (err) {
            console.error("Reconciliation compare failed:", err);
            el("rcPositions").innerHTML = `<tr><td colspan="10" class="rc-empty">Comparison failed: ${esc(err.message)}</td></tr>`;
        } finally {
            btn.disabled = false;
        }
    }

    async function loadIssues() {
        try {
            const res = await fetch(`${apiBaseUrl}/api/reconciliation/issues?days=7`);
            if (!res.ok) throw new Error(await res.text());
            const issues = await res.json();
            if (!issues.length) {
                el("rcIssues").innerHTML = `<tr><td colspan="7" class="rc-empty">No issues in the last 7 days.</td></tr>`;
                return;
            }
            el("rcIssues").innerHTML = issues.map(i => `<tr>
                <td>${esc(new Date(i.checkDate).toLocaleDateString("en-IN", { day: "2-digit", month: "short", year: "numeric" }))}</td>
                <td>${esc(CHECKS[i.checkType] || i.checkType)}</td>
                <td><strong>${esc(i.symbol || "")}</strong></td>
                <td class="num">${num(i.ours)}</td>
                <td class="num">${num(i.external)}</td>
                <td class="num">${i.diffPct == null ? "–" : (i.diffPct > 0 ? "+" : "") + Number(i.diffPct).toFixed(2) + "%"}</td>
                <td>${esc(i.details || "")}</td>
            </tr>`).join("");
        } catch (err) {
            console.error("Loading data-quality issues failed:", err);
            el("rcIssues").innerHTML = `<tr><td colspan="7" class="rc-empty">Could not load issues: ${esc(err.message)}</td></tr>`;
        }
    }

    el("rcCompare").addEventListener("click", comparePositions);
    loadIssues();
})();
