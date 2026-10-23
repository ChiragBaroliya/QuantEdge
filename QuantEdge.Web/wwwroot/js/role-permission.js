/**
 * Role Permissions (Admin): toggle which pages each role can open.
 * Data: GET /rolepermission/matrix?role=..., save: PUT /rolepermission/save.
 */
$(document.body).ready(function () {
    const swalTheme = { background: 'var(--bg-card)', color: 'var(--text-primary)' };

    let currentRole = 'User';
    let matrix = null;        // last loaded RolePermissionMatrixDto
    let savedState = {};      // pageKey -> allowed, as stored
    let workingState = {};    // pageKey -> allowed, as edited

    initRolePermissions();

    function initRolePermissions() {
        $('.rp-role-tab').on('click', function () {
            const role = $(this).data('role');
            if (role === currentRole) return;
            if (pendingCount() > 0 && !confirm('Discard unsaved changes for the ' + currentRole + ' role?')) return;
            selectRole(role);
        });

        $('#rpGroups').on('change', '.rp-toggle', function () {
            workingState[$(this).data('key')] = this.checked;
            refreshState();
        });

        $('#rpGroups').on('click', '.rp-group-all, .rp-group-none', function () {
            const allow = $(this).hasClass('rp-group-all');
            const group = $(this).data('group');
            editablePages().filter(p => p.group === group).forEach(p => { workingState[p.key] = allow; });
            renderGroups();
        });

        $('#btnRpReset').on('click', function () {
            workingState = Object.assign({}, savedState);
            renderGroups();
        });

        $('#btnRpDefaults').on('click', function () {
            editablePages().forEach(p => { workingState[p.key] = p.defaultAllowed; });
            renderGroups();
        });

        $('#btnRpSave').on('click', savePermissions);

        $(window).on('beforeunload', function () {
            if (pendingCount() > 0) return 'You have unsaved permission changes.';
        });

        loadMatrix();
    }

    function selectRole(role) {
        currentRole = role;
        $('.rp-role-tab').each(function () {
            const active = $(this).data('role') === role;
            $(this).toggleClass('active', active).attr('aria-selected', active);
        });
        $('#rpRoleNote').html(role === 'Admin'
            ? 'The <strong>Admin</strong> role always has access to every page. Shown for reference only.'
            : 'Turn pages on or off for the <strong>User</strong> role. Changes apply to signed-in users within a couple of minutes - no re-login needed.');
        loadMatrix();
    }

    function loadMatrix() {
        $('#rpGroups').html('<div class="rp-loading">Loading page permissions...</div>');
        $('#rpLastUpdated').text('');

        $.ajax({
            url: '/rolepermission/matrix?role=' + encodeURIComponent(currentRole),
            method: 'GET',
            dataType: 'json',
            success: function (data) {
                matrix = data;
                savedState = {};
                (data.pages || []).forEach(p => { savedState[p.key] = p.isAllowed; });
                workingState = Object.assign({}, savedState);
                renderLastUpdated();
                renderGroups();
            },
            error: function (xhr) {
                matrix = null;
                const msg = (xhr.responseJSON && xhr.responseJSON.message) || 'Unable to load role permissions.';
                $('#rpGroups').html('<div class="rp-error">' + escapeHtml(msg) + '</div>');
                refreshState();
            }
        });
    }

    function editablePages() {
        if (!matrix || matrix.isReadOnly) return [];
        return matrix.pages.filter(p => p.lock === 'None');
    }

    function renderGroups() {
        if (!matrix) return;

        const groups = [];
        matrix.pages.forEach(p => {
            let g = groups.find(x => x.name === p.group);
            if (!g) { g = { name: p.group, pages: [] }; groups.push(g); }
            g.pages.push(p);
        });

        const html = groups.map(g => {
            const allowedInGroup = g.pages.filter(p => workingState[p.key]).length;
            const anyEditable = !matrix.isReadOnly && g.pages.some(p => p.lock === 'None');
            const groupAttr = escapeHtml(g.name);

            return `
                <section class="rp-group">
                    <header class="rp-group-header">
                        <h3 class="rp-group-title">${escapeHtml(g.name)}<span class="rp-group-count">${allowedInGroup} / ${g.pages.length}</span></h3>
                        <div class="rp-group-actions">
                            <button type="button" class="rp-link-btn rp-group-all" data-group="${groupAttr}" ${anyEditable ? '' : 'disabled'}>All</button>
                            <button type="button" class="rp-link-btn rp-group-none" data-group="${groupAttr}" ${anyEditable ? '' : 'disabled'}>None</button>
                        </div>
                    </header>
                    ${g.pages.map(renderPageRow).join('')}
                </section>`;
        }).join('');

        $('#rpGroups').html(html);
        refreshState();
    }

    function renderPageRow(p) {
        const locked = matrix.isReadOnly || p.lock !== 'None';
        const checked = !!workingState[p.key];

        // The "Unsaved" badge and row highlight are added by refreshState().
        let badge = '';
        if (!matrix.isReadOnly && p.lock === 'AdminOnly') {
            badge = '<span class="rp-badge rp-badge-lock" title="Security page - only the Admin role can ever open it.">Admin only</span>';
        } else if (!matrix.isReadOnly && p.lock === 'AlwaysAllowed') {
            badge = '<span class="rp-badge rp-badge-always" title="Landing page - every signed-in user needs it.">Always on</span>';
        }

        const id = 'rp-toggle-' + p.key;
        return `
            <div class="rp-page-row">
                <label class="rp-page-info" for="${id}">
                    <div class="rp-page-name">${escapeHtml(p.name)} ${badge}</div>
                    <div class="rp-page-desc">${escapeHtml(p.description)}</div>
                </label>
                <span class="rp-switch">
                    <input type="checkbox" class="rp-toggle" id="${id}" data-key="${escapeHtml(p.key)}"
                           aria-label="${escapeHtml(p.name)} access" ${checked ? 'checked' : ''} ${locked ? 'disabled' : ''} />
                    <span class="rp-switch-track"></span>
                </span>
            </div>`;
    }

    function pendingCount() {
        return editablePages().filter(p => workingState[p.key] !== savedState[p.key]).length;
    }

    function refreshState() {
        const pages = matrix ? matrix.pages : [];
        const pending = pendingCount();

        $('#rpAllowedCount').text(matrix ? pages.filter(p => workingState[p.key]).length : '-');
        $('#rpTotalCount').text(matrix ? pages.length : '-');
        $('#rpPendingCount').text(pending);

        const readOnly = !matrix || matrix.isReadOnly;
        $('#btnRpSave').prop('disabled', readOnly || pending === 0);
        $('#btnRpReset').prop('disabled', readOnly || pending === 0);
        $('#btnRpDefaults').prop('disabled', readOnly);
        $('#rpSaveBar').toggleClass('rp-dirty', pending > 0);
        $('#rpSaveHint').text(readOnly
            ? (matrix ? 'The Admin role cannot be changed.' : 'Nothing to save.')
            : pending === 0 ? 'No unsaved changes.' : pending + ' unsaved change' + (pending > 1 ? 's' : '') + ' for the ' + currentRole + ' role.');

        // Keep the group counters in step without re-rendering every row.
        $('.rp-group').each(function () {
            const total = $(this).find('.rp-toggle').length;
            const on = $(this).find('.rp-toggle:checked').length;
            $(this).find('.rp-group-count').text(on + ' / ' + total);
        });
        $('.rp-toggle').each(function () {
            const key = $(this).data('key');
            const changed = !this.disabled && workingState[key] !== savedState[key];
            const row = $(this).closest('.rp-page-row').toggleClass('rp-changed', changed);
            const name = row.find('.rp-page-name');
            name.find('.rp-badge-edited').remove();
            if (changed) name.append('<span class="rp-badge rp-badge-edited">Unsaved</span>');
        });
    }

    function renderLastUpdated() {
        if (matrix && matrix.lastUpdatedAt) {
            const when = new Date(matrix.lastUpdatedAt).toLocaleString('en-IN', { dateStyle: 'medium', timeStyle: 'short' });
            $('#rpLastUpdated').text('Last changed ' + when + (matrix.lastUpdatedBy ? ' by ' + matrix.lastUpdatedBy : '') + '.');
        } else if (matrix && !matrix.isReadOnly) {
            $('#rpLastUpdated').text('Using built-in defaults - not changed yet.');
        }
    }

    function savePermissions() {
        if (!matrix || matrix.isReadOnly) return;

        const changes = editablePages().filter(p => workingState[p.key] !== savedState[p.key]);
        const granted = changes.filter(p => workingState[p.key]).map(p => p.name);
        const revoked = changes.filter(p => !workingState[p.key]).map(p => p.name);

        let summary = '';
        if (granted.length) summary += '<p><strong>Grant:</strong> ' + granted.map(escapeHtml).join(', ') + '</p>';
        if (revoked.length) summary += '<p><strong>Remove:</strong> ' + revoked.map(escapeHtml).join(', ') + '</p>';

        Swal.fire(Object.assign({
            icon: 'question',
            title: 'Save ' + currentRole + ' permissions?',
            html: summary,
            showCancelButton: true,
            confirmButtonText: 'Save',
            cancelButtonText: 'Cancel'
        }, swalTheme)).then(function (result) {
            if (!result.isConfirmed) return;

            const payload = {
                role: currentRole,
                allowedPageKeys: editablePages().filter(p => workingState[p.key]).map(p => p.key)
            };

            $('#btnRpSave').prop('disabled', true).text('Saving...');
            $.ajax({
                url: '/rolepermission/save',
                method: 'PUT',
                contentType: 'application/json',
                dataType: 'json',
                data: JSON.stringify(payload),
                success: function (res) {
                    Swal.fire(Object.assign({ icon: 'success', title: 'Saved', text: (res && res.message) || 'Permissions saved.' }, swalTheme));
                    loadMatrix();
                },
                error: function (xhr) {
                    const msg = (xhr.responseJSON && xhr.responseJSON.message) || 'Failed to save permissions.';
                    Swal.fire(Object.assign({ icon: 'error', title: 'Save Failed', text: msg }, swalTheme));
                    refreshState();
                },
                complete: function () {
                    $('#btnRpSave').text('Save changes');
                }
            });
        });
    }

    function escapeHtml(value) {
        return String(value ?? '')
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;')
            .replace(/'/g, '&#39;');
    }
});
