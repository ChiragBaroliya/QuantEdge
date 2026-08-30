/**
 * User Management JavaScript Module — QuantEdge
 * Handles server-side pagination, search filtering, user CRUD, modals, and notifications.
 */

$(document.body).ready(function () {
    let currentPage = 1;
    let currentPageSize = 25;
    let currentSearch = "";
    let currentRoleFilter = "all";
    let searchDebounceTimer = null;

    const currentUserId = parseInt($('#currentUserId').val() || "0");

    // Initialize module
    initUserManagement();

    function initUserManagement() {
        loadUserSummary();
        loadUserList(currentPage);

        // Filter event listeners
        $('#searchInputUser').on('input', function () {
            clearTimeout(searchDebounceTimer);
            searchDebounceTimer = setTimeout(function () {
                currentSearch = $('#searchInputUser').val().trim();
                currentPage = 1;
                loadUserList(currentPage);
            }, 300);
        });

        $('#roleFilterUser').on('change', function () {
            currentRoleFilter = $(this).val();
            currentPage = 1;
            loadUserList(currentPage);
        });

        $('#pageSizeUser').on('change', function () {
            currentPageSize = parseInt($(this).val());
            currentPage = 1;
            loadUserList(currentPage);
        });

        $('#btnRefreshUsers').on('click', function () {
            loadUserSummary();
            loadUserList(currentPage);
        });

        // Form submission handlers
        $('#formCreateUser').on('submit', handleCreateUser);
        $('#formEditUser').on('submit', handleEditUser);
        $('#formResetPassword').on('submit', handleResetPassword);

        // Delegated table button actions
        $('#userTableBody').on('click', '.btn-edit-user', function () {
            const id = $(this).data('id');
            const fullName = $(this).data('fullname');
            const username = $(this).data('username');
            const email = $(this).data('email') || '';
            const mobileNo = $(this).data('mobileno') || '';
            const role = $(this).data('role') || 'User';

            $('#editUserId').val(id);
            $('#editUsername').val(username);
            $('#editFullName').val(fullName);
            $('#editEmail').val(email);
            $('#editMobileNo').val(mobileNo);
            $('#editRole').val(role);

            const modal = new bootstrap.Modal(document.getElementById('modalEditUser'));
            modal.show();
        });

        $('#userTableBody').on('click', '.btn-reset-user-password', function () {
            const id = $(this).data('id');
            const username = $(this).data('username');

            $('#resetUserId').val(id);
            $('#resetUsernameText').text(username);
            $('#resetNewPassword').val('');
            $('#resetConfirmPassword').val('');

            const modal = new bootstrap.Modal(document.getElementById('modalResetPassword'));
            modal.show();
        });

        $('#userTableBody').on('click', '.btn-delete-user', function () {
            const id = $(this).data('id');
            const username = $(this).data('username');
            const role = $(this).data('role');

            if (id === currentUserId) {
                Swal.fire({
                    icon: 'warning',
                    title: 'Action Restricted',
                    text: 'You cannot delete your own active user session.',
                    confirmButtonColor: 'var(--theme-accent)',
                    background: 'var(--bg-card)',
                    color: 'var(--text-primary)'
                });
                return;
            }

            Swal.fire({
                title: 'Delete User Account?',
                html: `Are you sure you want to permanently delete user <strong>${escapeHtml(username)}</strong>?<br><span style="color:#f87171;font-size:12px;">This action cannot be undone.</span>`,
                icon: 'warning',
                showCancelButton: true,
                confirmButtonColor: '#dc2626',
                cancelButtonColor: '#475569',
                confirmButtonText: 'Yes, Delete User',
                background: 'var(--bg-card)',
                color: 'var(--text-primary)'
            }).then((result) => {
                if (result.isConfirmed) {
                    deleteUser(id, username);
                }
            });
        });
    }

    // Load Summary KPI counts
    function loadUserSummary() {
        $.ajax({
            url: '/user/summary',
            method: 'GET',
            dataType: 'json',
            success: function (data) {
                if (data) {
                    $('#kpiTotalUsers').text(data.totalUsers ?? 0);
                    $('#kpiAdminCount').text(data.adminCount ?? 0);
                    $('#kpiUserCount').text(data.userCount ?? 0);
                }
            },
            error: function (xhr) {
                console.error("Failed to load user summary KPI metrics:", xhr);
            }
        });
    }

    // Load Paginated User List
    function loadUserList(page) {
        currentPage = page;
        const tbody = $('#userTableBody');
        tbody.html(`
            <tr>
                <td colspan="7" style="text-align: center; padding: 40px; color: #94a3b8;">
                    <div class="spinner-border spinner-border-sm text-info me-2" role="status"></div>
                    Loading users...
                </td>
            </tr>
        `);

        $.ajax({
            url: '/user/list',
            method: 'GET',
            data: {
                search: currentSearch,
                roleFilter: currentRoleFilter,
                page: currentPage,
                pageSize: currentPageSize
            },
            dataType: 'json',
            success: function (response) {
                renderUserTable(response);
            },
            error: function (xhr) {
                console.error("Failed to load user list:", xhr);
                tbody.html(`
                    <tr>
                        <td colspan="7" style="text-align: center; padding: 40px; color: #f87171;">
                            Failed to load user list from server.
                        </td>
                    </tr>
                `);
            }
        });
    }

    // Render User Table
    function renderUserTable(data) {
        const tbody = $('#userTableBody');
        const items = data.items || [];
        const totalCount = data.totalCount || 0;
        const totalPages = data.totalPages || 0;
        const pageNumber = data.pageNumber || 1;
        const pageSize = data.pageSize || 25;

        tbody.empty();

        if (items.length === 0) {
            tbody.html(`
                <tr>
                    <td colspan="7" style="text-align: center; padding: 40px; color: #94a3b8;">
                        No users found matching current filter criteria.
                    </td>
                </tr>
            `);
            $('#paginationInfoUser').text("Showing 0 to 0 of 0 users");
            $('#paginationControlsUser').empty();
            return;
        }

        items.forEach(user => {
            const initial = (user.fullName || user.username || 'U').substring(0, 1).toUpperCase();
            const isAdminRole = (user.role || '').toLowerCase() === 'admin';
            const roleBadgeClass = isAdminRole ? 'admin-role' : 'user-role';
            const avatarBadgeClass = isAdminRole ? 'admin-avatar' : '';
            const createdFormatted = formatDate(user.createdAt);

            const isSelf = user.id === currentUserId;

            const tr = $(`
                <tr>
                    <td>
                        <div class="user-identity-cell">
                            <div class="user-avatar-badge ${avatarBadgeClass}">${escapeHtml(initial)}</div>
                            <div>
                                <div class="user-fullname-text">${escapeHtml(user.fullName)} ${isSelf ? '<span class="badge bg-primary text-white ms-1" style="font-size:10px;">You</span>' : ''}</div>
                                <div class="user-handle-text">@@${escapeHtml(user.username)}</div>
                            </div>
                        </div>
                    </td>
                    <td><span class="monospace text-info">${escapeHtml(user.username)}</span></td>
                    <td>${user.email ? escapeHtml(user.email) : '<span class="text-muted">—</span>'}</td>
                    <td>${user.mobileNo ? `<span style="color: #ffffff; font-family: monospace;">${escapeHtml(user.mobileNo)}</span>` : '<span class="text-muted">—</span>'}</td>
                    <td><span class="role-badge ${roleBadgeClass}">${escapeHtml(user.role)}</span></td>
                    <td><span class="user-registered-date" style="color: #ffffff !important; font-size: 12px; font-weight: 500;">${createdFormatted}</span></td>
                    <td>

                        <div class="action-btn-group">
                            <button type="button" class="btn-table-action btn-action-edit btn-edit-user" 
                                    data-id="${user.id}" 
                                    data-fullname="${escapeHtml(user.fullName)}" 
                                    data-username="${escapeHtml(user.username)}" 
                                    data-email="${escapeHtml(user.email || '')}" 
                                    data-mobileno="${escapeHtml(user.mobileNo || '')}" 
                                    data-role="${escapeHtml(user.role)}" 
                                    title="Edit User Details">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><path d="M11 4H4a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2v-7"></path><path d="M18.5 2.5a2.121 2.121 0 0 1 3 3L12 15l-4 1 1-4 9.5-9.5z"></path></svg>
                            </button>
                            <button type="button" class="btn-table-action btn-action-key btn-reset-user-password" 
                                    data-id="${user.id}" 
                                    data-username="${escapeHtml(user.username)}" 
                                    title="Reset Password">
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><rect x="3" y="11" width="18" height="11" rx="2" ry="2"></rect><path d="M7 11V7a5 5 0 0 1 10 0v4"></path></svg>
                            </button>
                            <button type="button" class="btn-table-action btn-action-delete btn-delete-user" 
                                    data-id="${user.id}" 
                                    data-username="${escapeHtml(user.username)}" 
                                    data-role="${escapeHtml(user.role)}" 
                                    title="Delete User" ${isSelf ? 'disabled style="opacity:0.3;cursor:not-allowed;"' : ''}>
                                <svg width="14" height="14" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2"><polyline points="3 6 5 6 21 6"></polyline><path d="M19 6v14a2 2 0 0 1-2 2H7a2 2 0 0 1-2-2V6m3 0V4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2"></path></svg>
                            </button>
                        </div>
                    </td>
                </tr>
            `);
            tbody.append(tr);
        });

        // Update pagination info
        const startRecord = (pageNumber - 1) * pageSize + 1;
        const endRecord = Math.min(pageNumber * pageSize, totalCount);
        $('#paginationInfoUser').text(`Showing ${startRecord} to ${endRecord} of ${totalCount} users`);

        renderPaginationControls(pageNumber, totalPages);
    }

    // Render Pagination Controls
    function renderPaginationControls(pageNumber, totalPages) {
        const controls = $('#paginationControlsUser');
        controls.empty();

        if (totalPages <= 1) return;

        // Previous button
        const prevBtn = $(`<button type="button" class="page-btn-user" ${pageNumber <= 1 ? 'disabled' : ''}>‹ Prev</button>`);
        prevBtn.on('click', function () {
            if (pageNumber > 1) loadUserList(pageNumber - 1);
        });
        controls.append(prevBtn);

        // Page number buttons logic
        let startPage = Math.max(1, pageNumber - 2);
        let endPage = Math.min(totalPages, startPage + 4);
        if (endPage - startPage < 4) {
            startPage = Math.max(1, endPage - 4);
        }

        for (let i = startPage; i <= endPage; i++) {
            const pageBtn = $(`<button type="button" class="page-btn-user ${i === pageNumber ? 'active' : ''}">${i}</button>`);
            pageBtn.on('click', function () {
                if (i !== pageNumber) loadUserList(i);
            });
            controls.append(pageBtn);
        }

        // Next button
        const nextBtn = $(`<button type="button" class="page-btn-user" ${pageNumber >= totalPages ? 'disabled' : ''}>Next ›</button>`);
        nextBtn.on('click', function () {
            if (pageNumber < totalPages) loadUserList(pageNumber + 1);
        });
        controls.append(nextBtn);
    }

    // Handle Create User
    function handleCreateUser(e) {
        e.preventDefault();
        const payload = {
            fullName: $('#createFullName').val().trim(),
            username: $('#createUsername').val().trim(),
            email: $('#createEmail').val().trim() || null,
            mobileNo: $('#createMobileNo').val().trim() || null,
            password: $('#createPassword').val(),
            role: $('#createRole').val()
        };

        const confirmPassword = $('#createConfirmPassword').val();
        if (payload.password !== confirmPassword) {
            Swal.fire({ icon: 'error', title: 'Validation Error', text: 'Passwords do not match.', background: 'var(--bg-card)', color: 'var(--text-primary)' });
            return;
        }

        $.ajax({
            url: '/user/create',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                const modalEl = document.getElementById('modalCreateUser');
                const modal = bootstrap.Modal.getInstance(modalEl);
                if (modal) modal.hide();

                $('#formCreateUser')[0].reset();
                Swal.fire({ icon: 'success', title: 'Success', text: 'New user created successfully!', background: 'var(--bg-card)', color: 'var(--text-primary)' });
                loadUserSummary();
                loadUserList(1);
            },
            error: function (xhr) {
                let errorMsg = 'Failed to create user.';
                if (xhr.responseJSON && xhr.responseJSON.message) {
                    errorMsg = xhr.responseJSON.message;
                }
                Swal.fire({ icon: 'error', title: 'Create Failed', text: errorMsg, background: 'var(--bg-card)', color: 'var(--text-primary)' });
            }
        });
    }

    // Handle Edit User
    function handleEditUser(e) {
        e.preventDefault();
        const id = parseInt($('#editUserId').val());
        const payload = {
            id: id,
            fullName: $('#editFullName').val().trim(),
            email: $('#editEmail').val().trim() || null,
            mobileNo: $('#editMobileNo').val().trim() || null,
            role: $('#editRole').val()
        };

        $.ajax({
            url: '/user/update/' + id,
            method: 'PUT',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                const modalEl = document.getElementById('modalEditUser');
                const modal = bootstrap.Modal.getInstance(modalEl);
                if (modal) modal.hide();

                Swal.fire({ icon: 'success', title: 'Updated', text: 'User details updated successfully!', background: 'var(--bg-card)', color: 'var(--text-primary)' });
                loadUserSummary();
                loadUserList(currentPage);
            },
            error: function (xhr) {
                let errorMsg = 'Failed to update user.';
                if (xhr.responseJSON && xhr.responseJSON.message) {
                    errorMsg = xhr.responseJSON.message;
                }
                Swal.fire({ icon: 'error', title: 'Update Failed', text: errorMsg, background: 'var(--bg-card)', color: 'var(--text-primary)' });
            }
        });
    }

    // Handle Reset Password
    function handleResetPassword(e) {
        e.preventDefault();
        const id = parseInt($('#resetUserId').val());
        const newPassword = $('#resetNewPassword').val();
        const confirmPassword = $('#resetConfirmPassword').val();

        if (newPassword !== confirmPassword) {
            Swal.fire({ icon: 'error', title: 'Validation Error', text: 'Passwords do not match.', background: 'var(--bg-card)', color: 'var(--text-primary)' });
            return;
        }

        const payload = {
            userId: id,
            newPassword: newPassword
        };

        $.ajax({
            url: '/user/reset-password/' + id,
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                const modalEl = document.getElementById('modalResetPassword');
                const modal = bootstrap.Modal.getInstance(modalEl);
                if (modal) modal.hide();

                Swal.fire({ icon: 'success', title: 'Password Reset', text: 'User password reset successfully!', background: 'var(--bg-card)', color: 'var(--text-primary)' });
            },
            error: function (xhr) {
                let errorMsg = 'Failed to reset password.';
                if (xhr.responseJSON && xhr.responseJSON.message) {
                    errorMsg = xhr.responseJSON.message;
                }
                Swal.fire({ icon: 'error', title: 'Reset Failed', text: errorMsg, background: 'var(--bg-card)', color: 'var(--text-primary)' });
            }
        });
    }

    // Delete User
    function deleteUser(id, username) {
        $.ajax({
            url: '/user/delete/' + id,
            method: 'DELETE',
            success: function (res) {
                Swal.fire({ icon: 'success', title: 'Deleted', text: `User '${username}' has been deleted.`, background: 'var(--bg-card)', color: 'var(--text-primary)' });
                loadUserSummary();
                loadUserList(currentPage);
            },
            error: function (xhr) {
                let errorMsg = 'Failed to delete user.';
                if (xhr.responseJSON && xhr.responseJSON.message) {
                    errorMsg = xhr.responseJSON.message;
                }
                Swal.fire({ icon: 'error', title: 'Delete Failed', text: errorMsg, background: 'var(--bg-card)', color: 'var(--text-primary)' });
            }
        });
    }

    // Helper functions
    function escapeHtml(text) {
        if (!text) return '';
        return text
            .replace(/&/g, "&amp;")
            .replace(/</g, "&lt;")
            .replace(/>/g, "&gt;")
            .replace(/"/g, "&quot;")
            .replace(/'/g, "&#039;");
    }

    function formatDate(dateStr) {
        if (!dateStr) return '—';
        try {
            const d = new Date(dateStr);
            if (isNaN(d.getTime())) return dateStr;
            return d.toLocaleDateString('en-US', { year: 'numeric', month: 'short', day: 'numeric', hour: '2-digit', minute: '2-digit' });
        } catch (e) {
            return dateStr;
        }
    }
});
