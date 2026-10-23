// QuantEdge Token Manager page scripts

function handleCreateTokenClick(event, btn) {
    // Show loading state
    btn.classList.add('loading');
    btn.querySelector('.btn-text').textContent = 'Connecting to API...';

    // Allow navigation to proceed (don't preventDefault)
    setTimeout(() => {
        // Reset if user comes back (e.g. cancelled Zerodha login)
        btn.classList.remove('loading');
        btn.querySelector('.btn-text').textContent = 'Create Token';
    }, 8000);
}

// ---- Zerodha API credentials form: inline validation + API Key change confirmation ----

// Kite Connect API keys and secrets are plain letters/digits (no spaces or symbols).
const CREDENTIAL_PATTERN = /^[A-Za-z0-9]+$/;
const API_KEY_LENGTH = { min: 6, max: 50 };
const API_SECRET_LENGTH = { min: 8, max: 100 };

function validateCredentialField(value, label, length, required) {
    if (!value) return required ? `${label} is required.` : "";
    if (/\s/.test(value)) return `${label} must not contain spaces.`;
    if (!CREDENTIAL_PATTERN.test(value)) return `${label} can contain only letters and numbers.`;
    if (value.length < length.min || value.length > length.max) {
        return `${label} must be ${length.min}–${length.max} characters (you entered ${value.length}).`;
    }
    return "";
}

function setFieldError(input, errorEl, message) {
    input.classList.toggle("is-invalid", !!message);
    input.setAttribute("aria-invalid", message ? "true" : "false");
    errorEl.textContent = message;
}

function maskKey(key) {
    if (!key) return "—";
    return key.length > 8 ? `${key.slice(0, 4)}••••${key.slice(-4)}` : key;
}

document.addEventListener("DOMContentLoaded", function () {
    const form = document.getElementById("credentialsForm");
    if (!form) return;

    const keyInput = document.getElementById("apiKey");
    const secretInput = document.getElementById("apiSecret");
    const keyError = document.getElementById("apiKeyError");
    const secretError = document.getElementById("apiSecretError");
    const saveBtn = document.getElementById("saveCredentialsBtn");

    const currentKey = (form.dataset.currentKey || "").trim();
    const hasSecret = form.dataset.hasSecret === "true";
    const hasActiveToken = form.dataset.hasActiveToken === "true";

    let confirmed = false;

    const keyChanged = () => currentKey !== "" && keyInput.value.trim() !== currentKey;

    // A new API Key belongs to a different Kite app, so its secret must be entered too.
    const secretRequired = () => !hasSecret || keyChanged();

    function validate() {
        const key = keyInput.value.trim();
        const secret = secretInput.value.trim();

        const keyMsg = validateCredentialField(key, "API Key", API_KEY_LENGTH, true);
        let secretMsg = validateCredentialField(secret, "API Secret", API_SECRET_LENGTH, secretRequired());
        if (!secretMsg && !secret && secretRequired() && keyChanged()) {
            secretMsg = "Enter the API Secret of the new Kite Connect app.";
        }
        if (!secretMsg && secret && secret === key) {
            secretMsg = "API Secret must be different from the API Key.";
        }

        setFieldError(keyInput, keyError, keyMsg);
        setFieldError(secretInput, secretError, secretMsg);
        return { ok: !keyMsg && !secretMsg, keyMsg, secretMsg };
    }

    // Validate a field as soon as the user leaves it, then live while they fix it.
    [keyInput, secretInput].forEach(input => {
        input.addEventListener("blur", () => { input.dataset.touched = "true"; validate(); });
        input.addEventListener("input", () => {
            if (input.dataset.touched === "true" || input.classList.contains("is-invalid")) validate();
        });
    });

    // Trim pasted values (copying from the Kite console often brings spaces / new lines along).
    [keyInput, secretInput].forEach(input => {
        input.addEventListener("paste", () => setTimeout(() => { input.value = input.value.trim(); validate(); }, 0));
    });

    // Saved credentials: "Change" opens the form, "Cancel" puts the saved values back and collapses it again.
    const summary = document.getElementById("credentialsSummary");
    const editor = document.getElementById("credentialsEditor");

    document.getElementById("editCredentialsBtn")?.addEventListener("click", () => {
        summary.hidden = true;
        editor.hidden = false;
        keyInput.focus();
        keyInput.select();
    });

    document.getElementById("cancelCredentialsBtn")?.addEventListener("click", () => {
        keyInput.value = currentKey;
        secretInput.value = "";
        secretInput.type = "password";
        [keyInput, secretInput].forEach(input => delete input.dataset.touched);
        setFieldError(keyInput, keyError, "");
        setFieldError(secretInput, secretError, "");
        editor.hidden = true;
        summary.hidden = false;
        document.getElementById("editCredentialsBtn")?.focus();
    });

    document.getElementById("toggleApiSecret")?.addEventListener("click", function () {
        const show = secretInput.type === "password";
        secretInput.type = show ? "text" : "password";
        this.classList.toggle("active", show);
        this.setAttribute("aria-label", show ? "Hide API Secret" : "Show API Secret");
        secretInput.focus();
    });

    // Keys are validated as letters/digits only, so they are safe to place in the dialog's HTML.
    function confirmKeyChange() {
        return qeConfirm({
            title: "Change Zerodha API Key?",
            html:
                `<div style="text-align:left;font-size:14px;line-height:1.6;">` +
                `<div style="margin-bottom:12px;font-family:monospace;">` +
                `${maskKey(currentKey)} <span style="color:var(--accent-amber);">→</span> ${maskKey(keyInput.value.trim())}</div>` +
                `<ul style="margin:0;padding-left:18px;">` +
                `<li>Today's Zerodha token is <b>removed</b>.</li>` +
                `<li>Auto Real trading for your account <b>pauses</b> until you click <b>Create Token</b> again.</li>` +
                `</ul></div>`,
            confirmButtonText: "Yes, change API Key",
            confirmButtonColor: "#ef4444"
        });
    }

    function submitForm() {
        keyInput.value = keyInput.value.trim();
        secretInput.value = secretInput.value.trim();
        saveBtn.classList.add("is-loading");
        saveBtn.textContent = "Saving...";
        confirmed = true;
        form.submit();
    }

    form.addEventListener("submit", async function (e) {
        if (confirmed) return;
        e.preventDefault();

        const result = validate();
        if (!result.ok) {
            (result.keyMsg ? keyInput : secretInput).focus();
            return;
        }

        if (!keyChanged() && currentKey !== "" && !secretInput.value.trim()) {
            qeToast("Nothing to save — the API Key is unchanged and no new API Secret was entered.", "info");
            return;
        }

        // Only a different API Key ends today's token; saving a new secret for the same key does not.
        if (hasActiveToken && keyChanged() && !(await confirmKeyChange())) {
            keyInput.focus();
            return;
        }

        submitForm();
    });
});

// "Re-create Token" (active session card): confirm before leaving for the Zerodha login page.
document.addEventListener("DOMContentLoaded", function () {
    const btn = document.getElementById("recreateTokenBtn");
    if (!btn) return;

    btn.addEventListener("click", async function (e) {
        e.preventDefault();
        const ok = await qeConfirm({
            icon: "question",
            title: "Re-create Zerodha token?",
            text: "You will be sent to the Zerodha login page. Today's current token keeps working until the new one is saved.",
            confirmButtonText: "Yes, continue to Zerodha"
        });
        if (ok) window.location.href = btn.href;
    });
});
