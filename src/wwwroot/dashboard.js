class VigilanteDashboard {
    constructor() {
        this.statusApiEndpoint = '/api/v1/cluster/status';
        this.sizesPaginatedApiEndpoint = '/api/v1/collections/info';
        this.snapshotsApiEndpoint = '/api/v1/snapshots/info';
        this.replicateShardsEndpoint = '/api/v1/cluster/replicate-shards';
        this.dropShardsEndpoint = '/api/v1/cluster/drop-shards';
        this.startReshardingEndpoint = '/api/v1/cluster/start-resharding';
        this.deleteCollectionEndpoint = '/api/v1/collections';
        this.setAliasEndpoint = '/api/v1/collections/alias';
        this.renameAliasEndpoint = '/api/v1/collections/alias/rename';
        this.deleteAliasEndpoint = '/api/v1/collections/alias/delete';
        this.createSnapshotEndpoint = '/api/v1/snapshots';
        this.deleteSnapshotEndpoint = '/api/v1/snapshots';
        this.downloadSnapshotEndpoint = '/api/v1/snapshots/download';
        this.recoverFromSnapshotEndpoint = '/api/v1/snapshots/recover';
        this.deletePodEndpoint = '/api/v1/kubernetes/delete-pod';
        this.removePeerEndpoint = '/api/v1/cluster/remove-peer';
        this.manageStatefulSetEndpoint = '/api/v1/kubernetes/manage-statefulset';
        this.restoreReplicationFactorEndpoint = '/api/v1/collections/restore-replication-factor';
        this.jobsStatusEndpoint = '/api/v1/jobs/status';
        this.jobsCancelEndpoint = '/api/v1/jobs/cancel';
        this.qdrantLogsEndpoint = '/api/v1/logs/qdrant';
        this.vigilanteLogsEndpoint = '/api/v1/logs/vigilante';
        this.environmentEndpoint = '/api/v1/config/environment';
        this.refreshInterval = 0;
        this.autoRefreshTimer = null;
        this.openSnapshots = new Set();
        this.openCollections = new Set(); // Track which collections are open
        this.selectedState = new Map();
        this.openNodeMenus = new Set(); // Track which node menus are open (by peerId)
        this.openCollectionMenus = new Set(); // Track which collection menus are open (by collection name)
        this.openSnapshotCollectionMenus = new Set(); // Track which snapshot collection menus are open (by collection name)
        this.openSnapshotMenus = new Set(); // Track which individual snapshot menus are open (by snapshot key)
        this.stickyActionsMenuOpen = false; // Track if sticky actions menu is open
        this.toastIdCounter = 0; // Counter for unique toast IDs
        this.clusterIssues = []; // Issues from cluster/status
        this.collectionIssues = []; // Issues from collections-info
        this.clusterNodes = []; // Store cluster nodes for StatefulSet management
        this.environment = 'Loading...'; // Current environment name
        this.namespace = 'Loading...'; // Current namespace
        // Logs state
        this.logsRefreshInterval = 0;
        this.logsRefreshTimer = null;
        this.currentLogContext = null; // { type: 'qdrant' | 'vigilante', podName?: string, namespace?: string }
        // Pagination state for collections
        this.currentPage = 1;
        this.pageSize = 10;
        this.totalPages = 1;
        this.collectionNameFilter = '';
        // Pagination state for snapshots
        this.snapshotCurrentPage = 1;
        this.snapshotPageSize = 10;
        this.snapshotTotalPages = 1;
        this.snapshotNameFilter = '';
        this.jobs = []; // Background jobs from GET /api/v1/jobs/status
        this.pendingJobCancellations = new Set();
        this._configCollectionNames = []; // For config modal override row pickers
        this.themeStorageKey = 'vigilante-theme';
        this.themeMediaQuery = null;
        this.init();
        this.setupRefreshControls();
        this.setupCollectionControls();
        this.setupSnapshotControls();
        this.setupLogsControls();
        this.setupConfigControls();
        this.setupThemeToggle();
        this.setupStickyActionsMenu();
    }

    // Convert numeric status to string
    getStatusText(status) {
        // Handle both numeric (old) and string (new) enum values
        if (typeof status === 'number') {
            const statusMap = {
                0: 'Healthy',
                1: 'Degraded', 
                2: 'Unavailable'
            };
            return statusMap[status] || 'Unknown';
        }
        // String enum values are already in correct format
        return status || 'Unknown';
    }

    getStatusClass(status) {
        // Handle both numeric (old) and string (new) enum values
        if (typeof status === 'number') {
            const classMap = {
                0: 'healthy',
                1: 'degraded',
                2: 'unavailable'
            };
            return classMap[status] || 'loading';
        }
        // Convert string enum to lowercase for CSS class
        return (status || 'loading').toLowerCase();
    }

    init() {
        // Load initial data but don't start auto-refresh by default
        this.loadClusterStatus();
        this.loadCollectionSizes();
        this.loadSnapshots();
        this.loadJobs();
        this.loadEnvironment();
        
        // Setup StatefulSet management button
        const manageStatefulSetBtn = document.getElementById('manageStatefulSetBtn');
        if (manageStatefulSetBtn) {
            manageStatefulSetBtn.addEventListener('click', () => {
                this.showStatefulSetDialog();
            });
        }
        
        // Setup recovery modal after DOM is ready
        this.setupRecoveryModal();
    }

    setupThemeToggle() {
        const themeToggleButton = document.getElementById('themeToggleButton');
        const savedTheme = localStorage.getItem(this.themeStorageKey);
        this.themeMediaQuery = window.matchMedia('(prefers-color-scheme: dark)');

        // Respect explicit user choice; otherwise follow OS preference.
        const initialTheme = savedTheme === 'dark' || savedTheme === 'light'
            ? savedTheme
            : (this.themeMediaQuery.matches ? 'dark' : 'light');

        this.applyTheme(initialTheme);

        themeToggleButton?.addEventListener('click', () => {
            const currentTheme = document.documentElement.dataset.theme === 'dark' ? 'dark' : 'light';
            const nextTheme = currentTheme === 'dark' ? 'light' : 'dark';
            localStorage.setItem(this.themeStorageKey, nextTheme);
            this.applyTheme(nextTheme);
        });

        this.themeMediaQuery.addEventListener('change', (event) => {
            // Auto-sync with OS theme only when user has not set explicit preference.
            if (localStorage.getItem(this.themeStorageKey)) {
                return;
            }

            this.applyTheme(event.matches ? 'dark' : 'light');
        });
    }

    applyTheme(theme) {
        const normalizedTheme = theme === 'dark' ? 'dark' : 'light';
        const themeToggleButton = document.getElementById('themeToggleButton');
        const themeToggleIcon = document.getElementById('themeToggleIcon');

        document.documentElement.dataset.theme = normalizedTheme;
        themeToggleButton?.setAttribute('aria-label', `Switch to ${normalizedTheme === 'dark' ? 'light' : 'dark'} mode`);
        themeToggleButton?.setAttribute('title', `Switch to ${normalizedTheme === 'dark' ? 'light' : 'dark'} mode`);

        if (themeToggleIcon) {
            themeToggleIcon.className = normalizedTheme === 'dark' ? 'fas fa-sun' : 'fas fa-moon';
        }
    }

    setupRefreshControls() {
        const intervalSelect = document.getElementById('refreshInterval');
        const manualRefreshBtn = document.getElementById('manualRefresh');
        const stickyIntervalSelect = document.getElementById('stickyRefreshInterval');
        const stickyManualRefreshBtn = document.getElementById('stickyManualRefresh');

        // Read initial value from HTML (15s by default)
        const initialInterval = parseInt(intervalSelect.value || '0');
        this.refreshInterval = initialInterval;
        
        // Sync sticky controls with main controls
        if (stickyIntervalSelect) {
            stickyIntervalSelect.value = intervalSelect.value;
        }
        
        // Start auto-refresh if interval is set
        if (initialInterval > 0) {
            console.log(`Starting auto-refresh with initial interval: ${initialInterval}ms`);
            this.startAutoRefresh();
        }

        // Handle interval changes from main control
        intervalSelect.addEventListener('change', (e) => {
            const newInterval = parseInt(e.target.value);
            this.refreshInterval = newInterval;
            
            // Sync sticky control
            if (stickyIntervalSelect) {
                stickyIntervalSelect.value = e.target.value;
            }
            
            this.stopAutoRefresh();
            if (newInterval > 0) {
                this.startAutoRefresh();
            }
        });

        // Handle interval changes from sticky control
        if (stickyIntervalSelect) {
            stickyIntervalSelect.addEventListener('change', (e) => {
                const newInterval = parseInt(e.target.value);
                this.refreshInterval = newInterval;
                
                // Sync main control
                intervalSelect.value = e.target.value;
                
                this.stopAutoRefresh();
                if (newInterval > 0) {
                    this.startAutoRefresh();
                }
            });
        }

        // Handle manual refresh from main button
        manualRefreshBtn.addEventListener('click', () => {
            this.refresh();
        });

        // Handle manual refresh from sticky button
        if (stickyManualRefreshBtn) {
            stickyManualRefreshBtn.addEventListener('click', () => {
                this.refresh();
            });
        }
    }

    setupCollectionControls() {
        const filterInput = document.getElementById('collectionNameFilter');
        const clearFilterBtn = document.getElementById('clearFilterBtn');
        const prevPageBtn = document.getElementById('prevPageBtn');
        const nextPageBtn = document.getElementById('nextPageBtn');
        const pageSizeSelect = document.getElementById('pageSizeSelect');

        // Filter input with debounce
        let filterTimeout;
        filterInput.addEventListener('input', (e) => {
            clearTimeout(filterTimeout);
            filterTimeout = setTimeout(() => {
                this.collectionNameFilter = e.target.value.trim();
                this.currentPage = 1; // Reset to first page when filter changes
                this.loadCollectionSizes();
            }, 300);
        });

        // Clear filter button
        clearFilterBtn.addEventListener('click', () => {
            filterInput.value = '';
            this.collectionNameFilter = '';
            this.currentPage = 1;
            this.loadCollectionSizes();
        });

        // Page size selector
        pageSizeSelect.addEventListener('change', (e) => {
            this.pageSize = parseInt(e.target.value);
            this.currentPage = 1; // Reset to first page when page size changes
            this.loadCollectionSizes();
        });

        // Pagination buttons
        prevPageBtn.addEventListener('click', () => {
            if (this.currentPage > 1) {
                this.currentPage--;
                this.loadCollectionSizes();
            }
        });

        nextPageBtn.addEventListener('click', () => {
            if (this.currentPage < this.totalPages) {
                this.currentPage++;
                this.loadCollectionSizes();
            }
        });
    }

    setupSnapshotControls() {
        const filterInput = document.getElementById('snapshotNameFilter');
        const clearFilterBtn = document.getElementById('clearSnapshotFilterBtn');
        const prevPageBtn = document.getElementById('prevSnapshotPageBtn');
        const nextPageBtn = document.getElementById('nextSnapshotPageBtn');
        const pageSizeSelect = document.getElementById('snapshotPageSizeSelect');

        // Filter input with debounce
        let filterTimeout;
        filterInput.addEventListener('input', (e) => {
            clearTimeout(filterTimeout);
            filterTimeout = setTimeout(() => {
                this.snapshotNameFilter = e.target.value.trim();
                this.snapshotCurrentPage = 1; // Reset to first page when filter changes
                this.loadSnapshots();
            }, 300);
        });

        // Clear filter button
        clearFilterBtn.addEventListener('click', () => {
            filterInput.value = '';
            this.snapshotNameFilter = '';
            this.snapshotCurrentPage = 1;
            this.loadSnapshots();
        });

        // Page size selector
        pageSizeSelect.addEventListener('change', (e) => {
            this.snapshotPageSize = parseInt(e.target.value);
            this.snapshotCurrentPage = 1; // Reset to first page when page size changes
            this.loadSnapshots();
        });

        // Pagination buttons
        prevPageBtn.addEventListener('click', () => {
            if (this.snapshotCurrentPage > 1) {
                this.snapshotCurrentPage--;
                this.loadSnapshots();
            }
        });

        nextPageBtn.addEventListener('click', () => {
            if (this.snapshotCurrentPage < this.snapshotTotalPages) {
                this.snapshotCurrentPage++;
                this.loadSnapshots();
            }
        });
    }

    updatePaginationControls() {
        const prevPageBtn = document.getElementById('prevPageBtn');
        const nextPageBtn = document.getElementById('nextPageBtn');
        const pageInfo = document.getElementById('pageInfo');

        prevPageBtn.disabled = this.currentPage <= 1;
        nextPageBtn.disabled = this.currentPage >= this.totalPages;
        pageInfo.textContent = `Page ${this.currentPage} of ${this.totalPages}`;
    }

    updateSnapshotPaginationControls() {
        const prevPageBtn = document.getElementById('prevSnapshotPageBtn');
        const nextPageBtn = document.getElementById('nextSnapshotPageBtn');
        const pageInfo = document.getElementById('snapshotPageInfo');

        prevPageBtn.disabled = this.snapshotCurrentPage <= 1;
        nextPageBtn.disabled = this.snapshotCurrentPage >= this.snapshotTotalPages;
        pageInfo.textContent = `Page ${this.snapshotCurrentPage} of ${this.snapshotTotalPages}`;
    }

    refresh() {
        this.loadClusterStatus();
        this.loadCollectionSizes(true); // Clear cache on manual/auto refresh
        this.loadSnapshots(true); // Clear cache on manual/auto refresh
        this.loadJobs();

        // Restore sticky actions menu state after refresh
        this.restoreStickyActionsMenuState();
    }

    startAutoRefresh() {
        // Clear any existing timer first
        this.stopAutoRefresh();
        
        if (this.refreshInterval > 0) {
            console.log(`Starting auto-refresh with interval: ${this.refreshInterval}ms`);
            this.autoRefreshTimer = setInterval(() => {
                console.log('Auto-refreshing...');
                this.refresh();
            }, this.refreshInterval);
        }
    }

    stopAutoRefresh() {
        if (this.autoRefreshTimer) {
            console.log('Stopping auto-refresh');
            clearInterval(this.autoRefreshTimer);
            this.autoRefreshTimer = null;
        }
    }

    restoreStickyActionsMenuState() {
        if (this.stickyActionsMenuOpen) {
            const menuButton = document.getElementById('stickyActionsMenuButton');
            const dropdown = document.getElementById('stickyActionsDropdown');
            
            if (menuButton && dropdown) {
                // Use setTimeout to ensure DOM is fully updated
                setTimeout(() => {
                    dropdown.classList.add('show');
                    menuButton.classList.add('active');
                    console.log('Restored sticky actions menu state');
                }, 0);
            }
        }
    }

    async loadEnvironment() {
        try {
            const response = await fetch(this.environmentEndpoint);
            if (response.ok) {
                const data = await response.json();
                this.environment = data.environment || 'Unknown';
                this.namespace = data.namespace || 'Unknown';
                this.updateEnvironmentDisplay();
            } else {
                console.warn('Failed to load environment:', response.status);
                this.environment = 'Unknown';
                this.namespace = 'Unknown';
                this.updateEnvironmentDisplay();
            }
        } catch (error) {
            console.error('Error loading environment:', error);
            this.environment = 'Unknown';
            this.namespace = 'Unknown';
            this.updateEnvironmentDisplay();
        }
    }

    updateEnvironmentDisplay() {
        const envElement = document.getElementById('environmentBadge');
        if (envElement) {
            envElement.textContent = this.environment;
            // Add color based on environment
            envElement.className = 'environment-badge';
            if (this.environment === 'Production') {
                envElement.classList.add('env-production');
            } else if (this.environment === 'Development') {
                envElement.classList.add('env-development');
            } else if (this.environment === 'Staging') {
                envElement.classList.add('env-staging');
            }
        }
        
        const namespaceElement = document.getElementById('namespaceBadge');
        if (namespaceElement) {
            namespaceElement.textContent = this.namespace;
        }
    }

    setupRecoveryModal() {
        console.log('Setting up recovery modal');
        const modal = document.getElementById('recoveryModal');
        const closeBtn = modal?.querySelector('.modal-close');
        const cancelBtn = document.getElementById('cancelRecovery');
        const confirmBtn = document.getElementById('confirmRecovery');

        console.log('Modal elements found:', { modal, closeBtn, cancelBtn, confirmBtn });

        if (!modal || !closeBtn || !cancelBtn || !confirmBtn) {
            console.error('Recovery modal elements not found! Modal will not work.');
            console.error('Missing elements:', {
                modal: !modal ? 'recoveryModal' : null,
                closeBtn: !closeBtn ? '.modal-close' : null,
                cancelBtn: !cancelBtn ? 'cancelRecovery' : null,
                confirmBtn: !confirmBtn ? 'confirmRecovery' : null
            });
            return;
        }

        // Close modal when clicking X or Cancel
        closeBtn.onclick = () => {
            console.log('Close button clicked');
            this.closeRecoveryModal();
        };
        cancelBtn.onclick = () => {
            console.log('Cancel button clicked');
            this.closeRecoveryModal();
        };

        // Close modal when clicking outside (use addEventListener instead of overwriting window.onclick)
        modal.addEventListener('click', (event) => {
            if (event.target === modal) {
                console.log('Clicked outside modal content');
                this.closeRecoveryModal();
            }
        });

        // Handle recovery confirmation
        confirmBtn.onclick = () => {
            console.log('Confirm button clicked');
            this.confirmRecovery();
        };
        
        console.log('Recovery modal setup complete');
    }

    async openRecoveryModal(snapshot, collectionName, snapshotName) {
        const isS3 = snapshot.source === 'S3Storage';

        // For S3 snapshots — fetch presigned URL first
        let snapshotUrl = null;
        if (isS3) {
            const toastId = this.showToast(`Generating URL for '${snapshotName}'...`, 'info', null, 0, true);
            try {
                const response = await fetch('/api/v1/snapshots/get-download-url', {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ collectionName, snapshotName, expirationHours: 1 })
                });
                const result = await response.json();
                this.removeToast(toastId);
                if (!response.ok || !result.success) {
                    throw new Error(result.message || 'Failed to generate download URL');
                }
                snapshotUrl = result.url;
            } catch (error) {
                this.removeToast(toastId);
                this.showToast(`Failed to get S3 URL: ${this.getErrorMessage(error)}`, 'error', null, 15000);
                return;
            }
        }

        // Build node selector options
        const buildNodeOptions = () => {
            let html = '<option value="">Select target node...</option>';
            (this.clusterNodes || []).forEach(node => {
                const url = node.nodeUrl || node.url;
                let label = '';
                if (node.podName && node.podName !== 'unknown') {
                    label = node.podName;
                    if (node.peerId) {
                        label += ` (${node.peerId.substring(0, 12)}...)`;
                    }
                } else {
                    const peerId = node.peerId ? node.peerId.substring(0, 12) + '...' : '';
                    label = peerId ? `${url} (${peerId})` : url;
                }
                html += `<option value="${url}">${label}</option>`;
            });
            return html;
        };

        // For K8s snapshots — node is fixed (same pod where snapshot lives)
        const snapshotNodeUrl = snapshot.nodeUrl;
        const hasFixedNode = !isS3
            && snapshotNodeUrl
            && snapshotNodeUrl !== 'unknown'
            && snapshotNodeUrl !== 'S3';

        const nodeFieldHtml = hasFixedNode
            ? `<div class="form-group">
                <label>Target Node:</label>
                <input type="text" value="${snapshot.podName && snapshot.podName !== 'unknown' ? snapshot.podName : snapshotNodeUrl}" class="form-input" readonly />
               </div>`
            : `<div class="form-group">
                <label for="recoverModalTargetNode">Target Node:</label>
                <select id="recoverModalTargetNode" class="form-select">${buildNodeOptions()}</select>
               </div>`;

        const urlFieldHtml = isS3
            ? `<div class="form-group">
                <label>Snapshot URL:</label>
                <input type="text" id="recoverModalSnapshotUrl" value="${snapshotUrl}" class="form-input" readonly />
               </div>`
            : '';

        const prioritySelectHtml = `
            <div class="form-group">
                <label for="recoverModalPriority">Snapshot Priority:</label>
                <select id="recoverModalPriority" class="form-select">
                    <option value="Snapshot" selected>Snapshot (prefer snapshot data)</option>
                    <option value="Replica">Replica (prefer existing data)</option>
                    <option value="NoSync">NoSync (restore without sync)</option>
                </select>
                <small class="form-hint">Source of truth for snapshot recovery</small>
            </div>`;

        const checksumFieldHtml = isS3
            ? `<div class="form-group">
                <label for="recoverModalChecksum">Checksum (optional):</label>
                <input type="text" id="recoverModalChecksum" placeholder="Enter snapshot checksum" class="form-input" />
               </div>`
            : '';

        const s3ExtraFieldsHtml = `
            ${checksumFieldHtml}
            ${prioritySelectHtml}
            <div class="form-group">
                <label class="checkbox-label">
                    <input type="checkbox" id="recoverModalWaitForResult" />
                    Wait for result
                </label>
            </div>`;

        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';

        const modal = document.createElement('div');
        modal.className = 'modal-dialog';
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-undo"></i> Recover Collection from Snapshot</h3>
                <button class="modal-close">&times;</button>
            </div>
            <div class="modal-body">
                <div class="form-group">
                    <label>Snapshot:</label>
                    <input type="text" value="${snapshotName}" class="form-input" readonly />
                </div>
                ${urlFieldHtml}
                <div class="form-group">
                    <label for="recoverModalCollectionName">Collection Name:</label>
                    <input type="text" id="recoverModalCollectionName" value="${collectionName}" class="form-input" required />
                </div>
                ${nodeFieldHtml}
                ${s3ExtraFieldsHtml}
            </div>
            <div class="modal-footer">
                <button class="btn-secondary modal-cancel">Cancel</button>
                <button class="btn-primary modal-submit"><i class="fas fa-undo"></i> Recover</button>
            </div>
        `;

        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        setTimeout(() => overlay.querySelector('#recoverModalCollectionName')?.focus(), 100);

        const closeModal = () => {
            overlay.classList.add('closing');
            setTimeout(() => overlay.remove(), 300);
        };

        overlay.querySelector('.modal-close').addEventListener('click', closeModal);
        overlay.querySelector('.modal-cancel').addEventListener('click', closeModal);
        overlay.addEventListener('click', (e) => { if (e.target === overlay) closeModal(); });

        let isSubmitting = false;
        const submitButton = overlay.querySelector('.modal-submit');

        submitButton.addEventListener('click', async () => {
            if (isSubmitting) return;
            isSubmitting = true;
            submitButton.disabled = true;

            // Read all values before closing
            const collectionNameVal = overlay.querySelector('#recoverModalCollectionName')?.value.trim();
            const targetNodeUrl = hasFixedNode
                ? snapshotNodeUrl
                : overlay.querySelector('#recoverModalTargetNode')?.value;

            if (!collectionNameVal) {
                this.showToast('Please enter a collection name', 'error', null, 15000);
                overlay.querySelector('#recoverModalCollectionName')?.focus();
                isSubmitting = false;
                submitButton.disabled = false;
                return;
            }

            if (!targetNodeUrl) {
                this.showToast('Please select a target node', 'error', null, 15000);
                isSubmitting = false;
                submitButton.disabled = false;
                return;
            }

            const priority = overlay.querySelector('#recoverModalPriority')?.value || 'Snapshot';
            const waitForResult = overlay.querySelector('#recoverModalWaitForResult')?.checked ?? false;
            closeModal();
            const targetNode = this.clusterNodes?.find(n => (n.nodeUrl || n.url) === targetNodeUrl);
            const podName = targetNode ? targetNode.podName : snapshot.podName;
            await this.recoverSnapshotFromNode(
                targetNodeUrl,
                collectionNameVal,
                snapshotName,
                podName,
                snapshot.source,
                collectionName,
                priority,
                waitForResult);
        });

        overlay.querySelector('#recoverModalCollectionName')?.addEventListener('keypress', (e) => {
            if (e.key === 'Enter') submitButton.click();
        });
    }

    closeRecoveryModal() {
        // Legacy stub — kept for compatibility; recovery now uses a dynamic modal
    }

    showManageAliasesModal(collection) {
        const collectionName = collection.name;
        let aliases = [...(collection.aliases || [])];

        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';

        const modal = document.createElement('div');
        modal.className = 'modal-dialog';
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-tags"></i><span class="modal-header-title">Manage Aliases — ${this.escapeHtml(collectionName)}</span></h3>
                <button class="modal-close">&times;</button>
            </div>
            <div class="modal-body">
                <div class="form-group">
                    <label>Current aliases</label>
                    <ul id="manageAliasesList" class="manage-aliases-list"></ul>
                    <p id="manageAliasesEmpty" class="manage-aliases-empty" style="display:none;">No aliases. Add one below.</p>
                </div>
                <div class="form-group">
                    <label for="manageAliasesNewName">Add new alias</label>
                    <div class="manage-aliases-add-row">
                        <input type="text" id="manageAliasesNewName" class="form-input" placeholder="Alias name" />
                        <button type="button" class="btn-primary manage-aliases-add-btn"><i class="fas fa-plus"></i> Add</button>
                    </div>
                </div>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn-secondary modal-close-btn">Close</button>
            </div>
        `;

        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        const listEl = overlay.querySelector('#manageAliasesList');
        const emptyEl = overlay.querySelector('#manageAliasesEmpty');

        const renderList = () => {
            listEl.innerHTML = '';
            aliases.forEach(alias => {
                const li = document.createElement('li');
                li.className = 'manage-aliases-item';
                li.dataset.alias = alias;
                const isEditing = li.classList.contains('editing');
                li.innerHTML = `
                    <span class="manage-aliases-name">${this.escapeHtml(alias)}</span>
                    <div class="manage-aliases-actions">
                        <button type="button" class="manage-aliases-btn rename" title="Rename"><i class="fas fa-pen"></i></button>
                        <button type="button" class="manage-aliases-btn delete" title="Delete"><i class="fas fa-trash-alt"></i></button>
                    </div>
                `;
                li.querySelector('.rename').addEventListener('click', (e) => {
                    e.stopPropagation();
                    startRename(li, alias);
                });
                li.querySelector('.delete').addEventListener('click', (e) => {
                    e.stopPropagation();
                    deleteAlias(alias);
                });
                listEl.appendChild(li);
            });
            emptyEl.style.display = aliases.length === 0 ? 'block' : 'none';
        };

        const startRename = (li, oldName) => {
            if (li.classList.contains('editing')) return;
            li.classList.add('editing');
            const nameSpan = li.querySelector('.manage-aliases-name');
            const actionsDiv = li.querySelector('.manage-aliases-actions');
            const oldContent = nameSpan.outerHTML + actionsDiv.outerHTML;
            li.innerHTML = `
                <input type="text" class="form-input manage-aliases-rename-input" value="${this.escapeHtml(oldName)}" />
                <div class="manage-aliases-rename-actions">
                    <button type="button" class="btn-secondary manage-aliases-save-rename">Save</button>
                    <button type="button" class="btn-secondary manage-aliases-cancel-rename">Cancel</button>
                </div>
            `;
            const input = li.querySelector('.manage-aliases-rename-input');
            input.focus();
            input.select();
            li.querySelector('.manage-aliases-save-rename').addEventListener('click', () => {
                const newName = input.value.trim();
                if (!newName) {
                    this.showToast('Alias name cannot be empty', 'error', null, 5000);
                    return;
                }
                if (newName === oldName) {
                    li.classList.remove('editing');
                    renderList();
                    return;
                }
                renameAlias(oldName, newName, li);
            });
            li.querySelector('.manage-aliases-cancel-rename').addEventListener('click', () => {
                li.classList.remove('editing');
                renderList();
            });
            input.addEventListener('keydown', (e) => {
                if (e.key === 'Enter') li.querySelector('.manage-aliases-save-rename').click();
                if (e.key === 'Escape') li.querySelector('.manage-aliases-cancel-rename').click();
            });
        };

        const closeModal = () => {
            overlay.classList.add('closing');
            setTimeout(() => overlay.remove(), 300);
            if (typeof this.loadCollectionSizes === 'function') this.loadCollectionSizes(true);
        };

        overlay.querySelector('.modal-close').addEventListener('click', closeModal);
        overlay.querySelector('.modal-close-btn').addEventListener('click', closeModal);
        overlay.addEventListener('click', (e) => { if (e.target === overlay) closeModal(); });

        const apiPost = async (url, body) => {
            const res = await fetch(url, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(body)
            });
            const data = await res.json().catch(() => ({}));
            if (!res.ok) {
                throw new Error(data.error || data.message || res.statusText || 'Request failed');
            }
            return data;
        };

        const addAlias = async () => {
            const input = overlay.querySelector('#manageAliasesNewName');
            const aliasName = input?.value?.trim();
            if (!aliasName) {
                this.showToast('Enter an alias name', 'error', null, 5000);
                input?.focus();
                return;
            }
            if (aliases.includes(aliasName)) {
                this.showToast(`Alias "${aliasName}" already exists`, 'error', null, 5000);
                return;
            }
            try {
                await apiPost(this.setAliasEndpoint, { collectionName, aliasName });
                aliases = [...aliases, aliasName].sort();
                renderList();
                input.value = '';
                this.showToast(`Alias "${aliasName}" added`, 'success', null, 3000);
                if (typeof this.loadCollectionSizes === 'function') this.loadCollectionSizes(true);
            } catch (err) {
                this.showToast(err.message || 'Failed to add alias', 'error', null, 10000);
            }
        };

        const renameAlias = async (oldName, newName, liEl) => {
            if (aliases.includes(newName) && newName !== oldName) {
                this.showToast(`Alias "${newName}" already exists`, 'error', null, 5000);
                return;
            }
            try {
                await apiPost(this.renameAliasEndpoint, { oldAliasName: oldName, newAliasName: newName });
                aliases = aliases.map(a => a === oldName ? newName : a).sort();
                liEl.classList.remove('editing');
                renderList();
                this.showToast(`Alias renamed to "${newName}"`, 'success', null, 3000);
                if (typeof this.loadCollectionSizes === 'function') this.loadCollectionSizes(true);
            } catch (err) {
                this.showToast(err.message || 'Failed to rename alias', 'error', null, 10000);
            }
        };

        const deleteAlias = async (aliasName) => {
            try {
                await apiPost(this.deleteAliasEndpoint, { aliasName });
                aliases = aliases.filter(a => a !== aliasName);
                renderList();
                this.showToast(`Alias "${aliasName}" deleted`, 'success', null, 3000);
                if (typeof this.loadCollectionSizes === 'function') this.loadCollectionSizes(true);
            } catch (err) {
                this.showToast(err.message || 'Failed to delete alias', 'error', null, 10000);
            }
        };

        overlay.querySelector('.manage-aliases-add-btn').addEventListener('click', addAlias);
        overlay.querySelector('#manageAliasesNewName').addEventListener('keypress', (e) => {
            if (e.key === 'Enter') addAlias();
        });

        renderList();
        setTimeout(() => overlay.querySelector('#manageAliasesNewName')?.focus(), 100);
    }

    // Toast notification methods
    showToast(message, type = 'info', title = null, duration = null, isLoading = false) {
        // Set default duration based on type if not specified
        if (duration === null) {
            duration = type === 'error' ? 15000 : 5000;
        }
        
        const container = document.getElementById('toast-container');
        if (!container) return null;

        const toastId = `toast-${this.toastIdCounter++}`;
        const toast = document.createElement('div');
        toast.className = `toast ${type}`;
        toast.id = toastId;

        const icons = {
            success: '<i class="fas fa-check-circle"></i>',
            error: '<i class="fas fa-exclamation-circle"></i>',
            warning: '<i class="fas fa-exclamation-triangle"></i>',
            info: '<i class="fas fa-info-circle"></i>'
        };

        const iconHtml = isLoading 
            ? '<div class="toast-spinner"></div>'
            : `<div class="toast-icon">${icons[type] || icons.info}</div>`;

        toast.innerHTML = `
            ${iconHtml}
            <div class="toast-content">
                ${title ? `<div class="toast-title">${title}</div>` : ''}
                <div class="toast-message">${message}</div>
            </div>
            ${!isLoading ? '<button class="toast-close" aria-label="Close">&times;</button>' : ''}
        `;

        const closeBtn = toast.querySelector('.toast-close');
        if (closeBtn) {
            closeBtn.addEventListener('click', () => this.removeToast(toastId));
        }

        container.appendChild(toast);

        // Auto remove after duration (if not loading)
        if (duration > 0 && !isLoading) {
            setTimeout(() => this.removeToast(toastId), duration);
        }

        return toastId;
    }

    removeToast(toastId) {
        const toast = document.getElementById(toastId);
        if (!toast) return;

        toast.classList.add('removing');
        setTimeout(() => {
            toast.remove();
        }, 300);
    }

    updateToast(toastId, message, type = 'info', title = null, progress = null, autoRemove = true) {
        const toast = document.getElementById(toastId);
        if (!toast) return;

        const icons = {
            success: '<i class="fas fa-check-circle"></i>',
            error: '<i class="fas fa-exclamation-circle"></i>',
            warning: '<i class="fas fa-exclamation-triangle"></i>',
            info: '<i class="fas fa-info-circle"></i>'
        };

        const isLoading = type === 'info' && progress !== null;
        const iconHtml = isLoading
            ? '<div class="toast-spinner"></div>'
            : `<div class="toast-icon">${icons[type] || icons.info}</div>`;

        const progressHtml = progress !== null && progress >= 0 && progress <= 100
            ? `<div class="toast-progress-container">
                <div class="toast-progress-bar" style="width: ${progress}%"></div>
               </div>`
            : '';

        toast.className = `toast ${type}`;
        toast.innerHTML = `
            ${iconHtml}
            <div class="toast-content">
                ${title ? `<div class="toast-title">${title}</div>` : ''}
                <div class="toast-message">${message}</div>
                ${progressHtml}
            </div>
            ${!isLoading ? '<button class="toast-close" aria-label="Close">&times;</button>' : ''}
        `;

        const closeBtn = toast.querySelector('.toast-close');
        if (closeBtn) {
            closeBtn.addEventListener('click', () => this.removeToast(toastId));
        }

        // Auto remove after 5 seconds only if autoRemove is true and not loading
        if (autoRemove && !isLoading) {
            setTimeout(() => this.removeToast(toastId), 5000);
        }
    }

    async loadClusterStatus() {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 30000); // 30 second timeout
        
        try {
            this.showRefreshAnimation();
            const response = await fetch(this.statusApiEndpoint, {
                signal: controller.signal
            });
            clearTimeout(timeoutId);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            this.updateUI(data);
            
        } catch (error) {
            clearTimeout(timeoutId);
            console.error('Error fetching cluster status:', error);
            
            // Add error to cluster issues instead of showing separate error message
            let errorMessage;
            if (error.name === 'AbortError') {
                errorMessage = 'Request timed out after 30 seconds. Please check your connection to the cluster.';
            } else {
                errorMessage = this.getErrorMessage(error);
            }
            this.addClusterError(errorMessage);
        } finally {
            this.hideRefreshAnimation();
        }
    }

    async loadJobs() {
        try {
            const response = await fetch(this.jobsStatusEndpoint);
            if (!response.ok) {
                throw new Error(`HTTP ${response.status}`);
            }
            const data = await response.json();
            this.jobs = Array.isArray(data) ? data : [];
            this.updateJobs();
        } catch (error) {
            console.error('Error loading jobs:', error);
            this.jobs = [];
            this.updateJobs();
        }
    }

    updateJobs() {
        const container = document.getElementById('jobsList');
        if (!container) return;

        if (this.jobs.length === 0) {
            container.innerHTML = '<p class="jobs-empty">No background jobs</p>';
            return;
        }

        container.innerHTML = this.jobs.map(job => {
            const key = job.key || 'unknown';
            const error = job.errorMessage || null;
            const errorAt = job.errorRecordedAt ? new Date(job.errorRecordedAt).toLocaleString() : '';
            const meta = job.metadata || {};
            const currentAction = meta.CurrentAction != null
                ? (Array.isArray(meta.CurrentAction) ? meta.CurrentAction.join(' · ') : String(meta.CurrentAction))
                : null;
            const startedAtRaw = meta.StartedAtUtc ?? null;
            const startedAt = startedAtRaw ? new Date(startedAtRaw).toLocaleString() : '';
            const metaRest = Object.entries(meta).filter(([k]) =>
                !['CurrentAction', 'StartedAtUtc', 'LastRunSummary', 'Phase', 'LastRunStartedUtc', 'LastCompletedUtc', 'LastRunSuccess', 'ReplicationPlan'].includes(k));
            const rawSummary = meta.LastRunSummary;
            const lastRunSummary = rawSummary != null && String(rawSummary).trim() !== ''
                ? String(rawSummary)
                : null;
            const metaStr = metaRest.length
                ? metaRest.map(([k, v]) => {
                    const val = Array.isArray(v) ? v.join(', ') : (typeof v === 'object' && v !== null ? JSON.stringify(v) : String(v));
                    return `${k}: ${val}`;
                }).join(' · ')
                : '';

            const phase = meta.Phase != null ? String(meta.Phase).toLowerCase() : null;
            const statusClass = error ? 'job-status-error' : (phase === 'idle' ? 'job-status-idle' : 'job-status-running');
            const statusText = error ? 'Error' : (phase === 'idle' ? 'Idle' : 'Running');
            const canCancel = !error && phase !== 'idle';
            const canDeleteError = !!error;
            const isCancelling = this.pendingJobCancellations.has(String(key));
            const lastLine = !error && meta.LastCompletedUtc
                ? `<div class="job-meta">${meta.LastRunSuccess === false ? 'Last run: failed' : 'Last run: OK'} · ${new Date(meta.LastCompletedUtc).toLocaleString()}</div>`
                : '';
            const errorBlock = error
                ? `<div class="job-error">${this.escapeHtml(error)}${errorAt ? ` <span class="job-error-at">${errorAt}</span>` : ''}</div>`
                : '';
            const currentActionBlock = currentAction ? `<div class="job-current-action">${this.escapeHtml(currentAction)}</div>` : '';
            const startedAtBlock = startedAt ? `<div class="job-meta">Started: ${this.escapeHtml(startedAt)}</div>` : '';
            const replicationPlanBlock = this.renderReplicationPlan(meta);
            const lastRunSummaryBlock = lastRunSummary
                ? `<div class="job-last-run-summary"><span class="job-last-run-label">Last run:</span> ${this.escapeHtml(lastRunSummary)}</div>`
                : '';
            const metaBlock = metaStr ? `<div class="job-meta">${this.escapeHtml(metaStr)}</div>` : '';
            const actionButtonBlock = canCancel
                ? `<button type="button" class="job-cancel-btn" data-job-key="${encodeURIComponent(String(key))}" data-job-action="cancel" ${isCancelling ? 'disabled' : ''}>${isCancelling ? 'Cancelling...' : 'Cancel'}</button>`
                : (canDeleteError
                    ? `<button type="button" class="job-cancel-btn" data-job-key="${encodeURIComponent(String(key))}" data-job-action="delete" ${isCancelling ? 'disabled' : ''}>${isCancelling ? 'Deleting...' : 'Delete'}</button>`
                    : '');

            return `
                <div class="job-item">
                    <div class="job-header">
                        <span class="job-key">${this.escapeHtml(key)}</span>
                        <div class="job-header-actions">
                            <span class="job-status ${statusClass}">${statusText}</span>
                            ${actionButtonBlock}
                        </div>
                    </div>
                    ${lastLine}
                    ${lastRunSummaryBlock}
                    ${currentActionBlock}
                    ${startedAtBlock}
                    ${replicationPlanBlock}
                    ${errorBlock}
                    ${metaBlock}
                </div>
            `;
        }).join('');

        container.querySelectorAll('.job-cancel-btn').forEach(button => {
            button.addEventListener('click', async (e) => {
                e.preventDefault();
                e.stopPropagation();
                const encodedKey = button.getAttribute('data-job-key');
                if (!encodedKey) return;
                const key = decodeURIComponent(encodedKey);
                const action = button.getAttribute('data-job-action') || 'cancel';
                if (this.pendingJobCancellations.has(key)) return;
                const confirmed = action === 'delete'
                    ? window.confirm(`Delete error job '${key}' from list?`)
                    : window.confirm(`Cancel job '${key}'?`);
                if (!confirmed) return;
                await this.cancelJobByKey(key);
            });
        });
    }

    normalizeReplicationAction(actionRaw, targetPeerId) {
        if (actionRaw == null) {
            return targetPeerId == null ? 'DropReplica' : 'AddReplica';
        }

        const value = String(actionRaw).trim().toLowerCase();
        if (value === 'addreplica' || value === 'add_replica' || value === 'add') return 'AddReplica';
        if (value === 'dropreplica' || value === 'drop_replica' || value === 'drop') return 'DropReplica';
        if (value === 'movereplica' || value === 'move_replica' || value === 'move') return 'MoveReplica';
        return String(actionRaw);
    }

    renderReplicationPlan(meta) {
        const rawPlan = meta.ReplicationPlan;
        if (!Array.isArray(rawPlan) || rawPlan.length === 0) {
            return '';
        }

        const normalized = rawPlan.map((step, idx) => {
            const actionRaw = step?.action ?? step?.replicatorAction;
            const targetPeerId = step?.targetPeerId ?? null;
            const stepNumber = step?.stepNumber ?? (idx + 1);
            return {
                stepNumber: Number.isFinite(Number(stepNumber)) ? Number(stepNumber) : (idx + 1),
                shardId: step?.shardId ?? '?',
                sourcePeerId: step?.sourcePeerId ?? '?',
                sourcePeerUri: step?.sourcePeerUri ?? null,
                targetPeerId: targetPeerId,
                targetPeerUri: step?.targetPeerUri ?? null,
                action: this.normalizeReplicationAction(actionRaw, targetPeerId)
            };
        }).sort((a, b) => a.stepNumber - b.stepNumber);

        const itemsHtml = normalized.map(p => {
            const srcPeer = this.escapeHtml(String(p.sourcePeerId));
            const srcUri = p.sourcePeerUri ? ` (${this.escapeHtml(String(p.sourcePeerUri))})` : '';
            const tgtPeer = p.targetPeerId == null ? '-' : this.escapeHtml(String(p.targetPeerId));
            const tgtUri = p.targetPeerUri ? ` (${this.escapeHtml(String(p.targetPeerUri))})` : '';
            const action = this.escapeHtml(String(p.action));
            const shardId = this.escapeHtml(String(p.shardId));
            return `<div class="job-meta">#${p.stepNumber} · ${action} · shard ${shardId} · ${srcPeer}${srcUri} -> ${tgtPeer}${tgtUri}</div>`;
        }).join('');

        return `<div class="job-meta"><strong>Replication plan (${normalized.length}):</strong></div>${itemsHtml}`;
    }

    async loadCollectionSizes(clearCache = false) {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 30000); // 30 second timeout
        
        try {
            // Build URL with pagination and filter parameters
            const params = new URLSearchParams({
                page: this.currentPage.toString(),
                pageSize: this.pageSize.toString(),
                clearCache: clearCache.toString()
            });
            
            if (this.collectionNameFilter) {
                params.append('nameFilter', this.collectionNameFilter);
            }
            
            const url = `${this.sizesPaginatedApiEndpoint}?${params.toString()}`;
            const response = await fetch(url, {
                signal: controller.signal
            });
            clearTimeout(timeoutId);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            
            // Extract pagination info from pagination object
            const pagination = data.pagination || {};
            this.currentPage = pagination.currentPage || 1;
            this.totalPages = pagination.totalPages || 1;
            this.pageSize = pagination.pageSize || 10;
            
            // Extract collections
            const collections = data.collections || [];
            
            // Extract collection issues if present
            this.collectionIssues = data.issues || [];
            
            // Update combined issues display
            this.updateCombinedIssues();
            
            // Update pagination controls
            this.updatePaginationControls();
            
            // Update total count with actual total from pagination
            const totalCountElement = document.getElementById('totalCollectionsCount');
            if (totalCountElement) {
                totalCountElement.textContent = `Collections: ${pagination.totalItems || 0}`;
            }
            
            this.updateCollectionSizes(collections);
            
        } catch (error) {
            clearTimeout(timeoutId);
            console.error('Error fetching collection sizes:', error);
            
            // Add error message to collection issues
            let errorMessage;
            if (error.name === 'AbortError') {
                errorMessage = 'Collections request timed out after 30 seconds. Please check your connection.';
            } else {
                errorMessage = `Error loading collections: ${this.getErrorMessage(error)}`;
            }
            
            if (!this.collectionIssues.includes(errorMessage)) {
                this.collectionIssues.push(errorMessage);
                this.updateCombinedIssues();
            }
        }
    }

    formatMetricValue(key, value, nodeInfo) {
        if (key === 'shards') {
            if (!Array.isArray(value)) return value;
            
            // Handle both old format (array of numbers) and new format (array of objects)
            const shardsHtml = value.map(shard => {
                let shardId, state, prettySize, prettyVectorsSize, prettyPayloadsSize, vectorsSizeBytes, payloadsSizeBytes;
                
                // Check if it's the new format (object with shardId, state, sizeBytes)
                if (typeof shard === 'object' && shard !== null && 'shardId' in shard) {
                    shardId = shard.shardId;
                    state = shard.state || 'Unknown';
                    prettySize = shard.prettySize ?? null;
                    prettyVectorsSize = shard.prettyVectorsSize ?? null;
                    prettyPayloadsSize = shard.prettyPayloadsSize ?? null;
                    vectorsSizeBytes = shard.vectorsSizeBytes ?? 0;
                    payloadsSizeBytes = shard.payloadsSizeBytes ?? 0;
                } else {
                    // Old format: just a number
                    shardId = shard;
                    const shardStates = nodeInfo?.metrics?.shardStates || {};
                    state = shardStates[shardId.toString()] || 'Unknown';
                    prettySize = null;
                    prettyVectorsSize = null;
                    prettyPayloadsSize = null;
                    vectorsSizeBytes = 0;
                    payloadsSizeBytes = 0;
                }
                
                const stateClass = state.toLowerCase().replace(/\s+/g, '-');
                const hasTelemetry = prettyVectorsSize != null && prettyPayloadsSize != null;
                const totalData = vectorsSizeBytes + payloadsSizeBytes;
                const vectorsPct = totalData > 0 ? Math.round((vectorsSizeBytes / totalData) * 100) : 50;
                const payloadPct = totalData > 0 ? 100 - vectorsPct : 50;
                const sizeDisplay = (() => {
                    if (hasTelemetry) {
                        const barHtml = `<span class="shard-size-bar" title="Vectors vs Payload"><span class="shard-size-bar-vectors" style="width:${vectorsPct}%"></span><span class="shard-size-bar-payload" style="width:${payloadPct}%"></span></span>`;
                        const legend = `<span class="shard-size-legend-line">${prettyVectorsSize} vectors</span><span class="shard-size-legend-line">${prettyPayloadsSize} payload</span>`;
                        const diskPart = prettySize != null ? ` <span class="shard-size-disk">${prettySize} on disk</span>` : '';
                        return `<span class="shard-size shard-size-with-breakdown">${barHtml}<span class="shard-size-legend">${legend}</span>${diskPart}</span>`;
                    }
                    if (prettySize != null) {
                        return `<span class="shard-size">${prettySize}</span>`;
                    }
                    return '';
                })();
                
                return `
                    <div class="shard-item">
                        <label class="shard-label">
                            <input type="checkbox" class="shard-checkbox" data-shard-id="${shardId}">
                            <div class="shard-label-content">
                                <div class="shard-info">
                                    <span class="shard-id">Shard ${shardId}</span>
                                    <span class="shard-state ${stateClass}">${state}</span>
                                </div>
                                ${sizeDisplay}
                            </div>
                        </label>
                    </div>
                `;
            }).join('');
            
            return `
                <div class="shards-container">
                    <div class="shards-section">
                        <div class="shards-label">Shards</div>
                        <label class="select-all-shards-label">
                            <input type="checkbox" class="select-all-shards-checkbox">
                            Select All
                        </label>
                        <div class="shards-grid">
                            ${shardsHtml}
                        </div>
                    </div>
                </div>
            `;
        }
        if (key === 'outgoingTransfers') {
            if (!Array.isArray(value) || value.length === 0) return '';
            return value.map(transfer => {
                const transferType = transfer.isSync ? 'Syncing' : 'Moving';
                const transferId = `${nodeInfo.peerId}-${transfer.shardId}-${transfer.toPeerId}`;
                const method = transfer.method || 'Unknown';
                const methodClass = method.toLowerCase().replace(/\s+/g, '-');
                return `
                    <div class="transfer-item" data-transfer-id="${transferId}">
                        <span class="transfer-info">${transferType} shard ${transfer.shardId} → ${transfer.to}</span>
                        <div class="transfer-actions">
                            <span class="transfer-method ${methodClass}">${method}</span>
                            <button class="abort-transfer-button" 
                                    data-shard-id="${transfer.shardId}" 
                                    data-source-peer="${nodeInfo.peerId}" 
                                    data-target-peer="${transfer.toPeerId}"
                                    title="Abort this transfer">
                                <i class="fas fa-stop-circle"></i> Abort
                            </button>
                        </div>
                    </div>`;
            }).join('');
        }
        // Hide shardStates and sizeBytes from metrics display
        if (key === 'shardStates' || key === 'shard_states' || key === 'sizeBytes') {
            return '';
        }
        return value;
    }

    formatSize(bytes) {
        if (!bytes) return '0 B';
        const sizes = ['B', 'KB', 'MB', 'GB', 'TB'];
        const i = Math.floor(Math.log(bytes) / Math.log(1024));
        return `${(bytes / Math.pow(1024, i)).toFixed(2)} ${sizes[i]}`;
    }

    saveShardSelection(stateKey, shardCheckboxes) {
        const selectedShards = new Set(
            Array.from(shardCheckboxes)
                .filter(cb => cb.checked)
                .map(cb => parseInt(cb.dataset.shardId))
        );
        this.selectedState.set(stateKey, { selectedShards });
        console.log(`Saved shard selection for ${stateKey}:`, Array.from(selectedShards));
    }

    clearOtherNodesShardSelection(currentStateKey) {
        // Clear shard selection on all other nodes
        document.querySelectorAll('[data-state-key]').forEach(nodeDetails => {
            const stateKey = nodeDetails.getAttribute('data-state-key');
            if (stateKey && stateKey !== currentStateKey) {
                // Uncheck all shard checkboxes
                nodeDetails.querySelectorAll('.shard-checkbox').forEach(cb => {
                    cb.checked = false;
                });
                
                // Uncheck Select All
                const selectAllCheckbox = nodeDetails.querySelector('.select-all-shards-checkbox');
                if (selectAllCheckbox) {
                    selectAllCheckbox.checked = false;
                    selectAllCheckbox.indeterminate = false;
                }
                
                // Clear state
                this.selectedState.delete(stateKey);
            }
        });
    }

    updateCollectionSizes(collections) {
        if (!Array.isArray(collections)) {
            console.warn('Received non-array collections data:', collections);
            collections = [];
        }
        
        // Save currently open collection menus before updating DOM
        this.openCollectionMenus.clear();
        document.querySelectorAll('.collection-actions-menu-button.active').forEach(btn => {
            const row = btn.closest('.collection-row');
            if (row) {
                const nameCell = row.querySelector('.collection-name-line');
                if (nameCell) {
                    const collectionName = nameCell.textContent?.trim();
                    if (collectionName) {
                        this.openCollectionMenus.add(collectionName);
                        console.log('Saved open menu state for collection:', collectionName);
                    }
                }
            }
        });
        
        // Calculate total size for current page
        let totalSizeBytes = 0;
        collections.forEach(info => {
            if (info?.metrics?.sizeBytes) {
                totalSizeBytes += info.metrics.sizeBytes;
            }
        });
        
        // Update total size display (for current page)
        const totalSizeElement = document.getElementById('totalCollectionsSize');
        if (totalSizeElement) {
            totalSizeElement.textContent = `Total Size: ${this.formatSize(totalSizeBytes)}`;
        }
        
        // Use persistent state to remember which collections were open
        // (instead of querying DOM which can have timing issues)
        console.log('Saving open collections state:', Array.from(this.openCollections));
        
        // Group collections by name, preserving backend node order
        const collectionsByName = collections.reduce((acc, info) => {
            if (!info || !info.collectionName) {
                console.warn('Invalid collection info:', info);
                return acc;
            }

            if (!acc[info.collectionName]) {
                acc[info.collectionName] = {
                    name: info.collectionName,
                    aliases: info.aliases || [],
                    status: info.status, // Save collection status (Green/Yellow/Red)
                    warnings: [],
                    nodes: [] // Use array to preserve backend order
                };
            }

            if (Array.isArray(info.warnings) && info.warnings.length > 0) {
                info.warnings.forEach(warning => {
                    if (warning && !acc[info.collectionName].warnings.includes(warning)) {
                        acc[info.collectionName].warnings.push(warning);
                    }
                });
            }

            // Use podName if available and not 'unknown', otherwise use peerId (match backend logic)
            const nodeKey = (info.podName && info.podName !== 'unknown') 
                ? info.podName 
                : (info.peerId || info.nodeUrl);
            
            acc[info.collectionName].nodes.push({
                nodeKey: nodeKey,
                size: info.metrics?.size || 0,
                podName: info.podName,
                peerId: info.peerId || '',
                nodeUrl: info.nodeUrl || '',
                podNamespace: info.podNamespace || '',
                metrics: info.metrics || {}
            });
            return acc;
        }, {});
        
        console.log('Collections grouped by name:', collectionsByName);
        console.log('Total unique collections:', Object.keys(collectionsByName).length);
        Object.entries(collectionsByName).forEach(([name, collection]) => {
            console.log(`Collection ${name} has ${collection.nodes.length} nodes`);
        });

        // Get unique node keys in the order they appear in backend response (already sorted)
        const nodeKeys = [...new Set(collections.map(info => {
            // Match backend sorting logic: prefer podName over peerId
            if (info.podName && info.podName !== 'unknown') {
                return info.podName;
            }
            return info.peerId || info.nodeUrl;
        }).filter(Boolean))]; // No .sort() - preserve backend order!
        const table = document.createElement('table');
        table.className = 'collections-table';
        const tbody = document.createElement('tbody');

        Object.values(collectionsByName)
            .sort((a, b) => a.name.localeCompare(b.name))
            .forEach(collection => {
                const row = document.createElement('tr');
                row.className = 'collection-row';
                
                // Calculate total size for this collection across all nodes
                let collectionTotalSize = 0;
                const uniqueShards = new Set();
                collection.nodes.forEach(nodeInfo => {
                    if (nodeInfo.metrics?.sizeBytes) {
                        collectionTotalSize += nodeInfo.metrics.sizeBytes;
                    }
                    // Collect unique shard IDs from all nodes
                    if (nodeInfo.metrics?.shards && Array.isArray(nodeInfo.metrics.shards)) {
                        nodeInfo.metrics.shards.forEach(shard => {
                            // Handle both old format (number) and new format (object with shardId)
                            const shardId = typeof shard === 'object' ? shard.shardId : shard;
                            uniqueShards.add(shardId);
                        });
                    }
                });
                const collectionTotalShards = uniqueShards.size;
                
                const nameCell = document.createElement('td');
                nameCell.className = 'collection-name';
                nameCell.colSpan = nodeKeys.length + 1;
                
                // Create a container for the entire collection header
                const headerContainer = document.createElement('div');
                headerContainer.className = 'collection-header-container';
                headerContainer.style.display = 'flex';
                headerContainer.style.justifyContent = 'space-between';
                headerContainer.style.alignItems = 'center';
                
                // Left side: Collection name and aliases
                const nameContainer = document.createElement('div');
                nameContainer.style.display = 'flex';
                nameContainer.style.flexDirection = 'column';
                nameContainer.style.gap = '4px';
                
                // Collection name with copy button
                const nameDiv = document.createElement('div');
                nameDiv.className = 'collection-name-line';
                nameDiv.style.display = 'flex';
                nameDiv.style.alignItems = 'center';
                nameDiv.style.gap = '8px';
                
                const nameText = document.createElement('span');
                nameText.textContent = collection.name;
                nameText.title = collection.name;
                nameDiv.appendChild(nameText);
                
                // Copy to clipboard button
                const copyButton = document.createElement('button');
                copyButton.className = 'collection-name-copy-btn';
                copyButton.innerHTML = '<i class="fas fa-copy"></i>';
                copyButton.title = 'Copy collection name to clipboard';
                copyButton.addEventListener('click', (e) => {
                    e.stopPropagation(); // Prevent row click
                    
                    // Try modern clipboard API first, fallback for HTTP contexts
                    if (navigator.clipboard && navigator.clipboard.writeText) {
                        navigator.clipboard.writeText(collection.name).then(() => {
                            this.showToast(`Collection name "${collection.name}" copied to clipboard`, 'success', 'Copied', 2000);
                        }).catch(err => {
                            console.error('Failed to copy:', err);
                            this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                        });
                    } else {
                        // Fallback for HTTP contexts where Clipboard API is not available
                        try {
                            const textArea = document.createElement('textarea');
                            textArea.value = collection.name;
                            textArea.style.position = 'fixed';
                            textArea.style.left = '-999999px';
                            textArea.style.top = '-999999px';
                            document.body.appendChild(textArea);
                            textArea.focus();
                            textArea.select();
                            const successful = document.execCommand('copy');
                            document.body.removeChild(textArea);
                            
                            if (successful) {
                                this.showToast(`Collection name "${collection.name}" copied to clipboard`, 'success', 'Copied', 2000);
                            } else {
                                this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                            }
                        } catch (err) {
                            console.error('Fallback: Could not copy text', err);
                            this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                        }
                    }
                });
                nameDiv.appendChild(copyButton);
                
                nameContainer.appendChild(nameDiv);
                
                // Aliases (if any)
                if (collection.aliases && collection.aliases.length > 0) {
                    const aliasesContainer = document.createElement('div');
                    aliasesContainer.className = 'collection-aliases-container';
                    aliasesContainer.style.display = 'flex';
                    aliasesContainer.style.alignItems = 'center';
                    aliasesContainer.style.gap = '6px';
                    aliasesContainer.style.flexWrap = 'wrap';
                    
                    const aliasesLabel = document.createElement('span');
                    aliasesLabel.className = 'aliases-label';
                    aliasesLabel.style.fontSize = '0.8em';
                    aliasesLabel.style.color = '#999';
                    aliasesLabel.textContent = 'Aliases:';
                    aliasesContainer.appendChild(aliasesLabel);
                    
                    collection.aliases.forEach(alias => {
                        const aliasBadge = document.createElement('div');
                        aliasBadge.className = 'alias-badge';
                        aliasBadge.style.display = 'inline-flex';
                        aliasBadge.style.alignItems = 'center';
                        aliasBadge.style.gap = '4px';
                        aliasBadge.style.padding = '2px 6px';
                        aliasBadge.style.background = '#e3f2fd';
                        aliasBadge.style.borderRadius = '4px';
                        aliasBadge.style.fontSize = '0.8em';
                        
                        const aliasText = document.createElement('span');
                        aliasText.textContent = alias;
                        aliasText.style.color = '#1976d2';
                        aliasText.style.fontStyle = 'italic';
                        
                        const copyAliasButton = document.createElement('button');
                        copyAliasButton.className = 'alias-copy-btn';
                        copyAliasButton.innerHTML = '<i class="fas fa-copy"></i>';
                        copyAliasButton.title = `Copy alias "${alias}" to clipboard`;
                        copyAliasButton.addEventListener('click', (e) => {
                            e.stopPropagation();
                            
                            if (navigator.clipboard && navigator.clipboard.writeText) {
                                navigator.clipboard.writeText(alias).then(() => {
                                    this.showToast(`Alias "${alias}" copied to clipboard`, 'success', 'Copied', 2000);
                                }).catch(err => {
                                    console.error('Failed to copy:', err);
                                    this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                                });
                            } else {
                                // Fallback for HTTP contexts where Clipboard API is not available
                                try {
                                    const textArea = document.createElement('textarea');
                                    textArea.value = alias;
                                    textArea.style.position = 'fixed';
                                    textArea.style.left = '-999999px';
                                    textArea.style.top = '-999999px';
                                    document.body.appendChild(textArea);
                                    textArea.focus();
                                    textArea.select();
                                    const successful = document.execCommand('copy');
                                    document.body.removeChild(textArea);
                                    
                                    if (successful) {
                                        this.showToast(`Alias "${alias}" copied to clipboard`, 'success', 'Copied', 2000);
                                    } else {
                                        this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                                    }
                                } catch (err) {
                                    console.error('Fallback: Could not copy text', err);
                                    this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                                }
                            }
                        });
                        
                        aliasBadge.appendChild(aliasText);
                        aliasBadge.appendChild(copyAliasButton);
                        aliasesContainer.appendChild(aliasBadge);
                    });
                    
                    nameContainer.appendChild(aliasesContainer);
                }
                
                // Right side: Status, Size and shards info
                const infoContainer = document.createElement('div');
                infoContainer.style.display = 'flex';
                infoContainer.style.flexDirection = 'column';
                infoContainer.style.alignItems = 'flex-end';
                infoContainer.style.gap = '4px';
                
                // Top row: Status and Size on same line
                const topRow = document.createElement('div');
                topRow.style.display = 'flex';
                topRow.style.flexDirection = 'row';
                topRow.style.alignItems = 'center';
                topRow.style.gap = '8px';

                // Backend provides collection warnings text.
                const showWarningIcon = Array.isArray(collection.warnings) && collection.warnings.length > 0;
                if (showWarningIcon) {
                    const tooltipText = collection.warnings.join('; ');
                    const warningIcon = document.createElement('span');
                    warningIcon.className = 'collection-status-warning-icon';
                    warningIcon.innerHTML = '<i class="fas fa-exclamation-triangle"></i>';
                    warningIcon.setAttribute('data-tooltip', tooltipText);
                    warningIcon.setAttribute('aria-label', `Warning: ${tooltipText}`);
                    topRow.appendChild(warningIcon);
                }
                
                // Collection Status (if available)
                if (collection.status) {
                    const statusSpan = document.createElement('span');
                    statusSpan.className = `collection-status collection-status-${collection.status.toLowerCase()}`;
                    
                    // Add icon and tooltip based on status
                    let icon = '';
                    let tooltipText = '';
                    switch (collection.status) {
                        case 'Green':
                            icon = '<i class="fas fa-check-circle"></i>';
                            tooltipText = 'All the points are processed and indexing is done, collection is ready';
                            break;
                        case 'Yellow':
                            icon = '<i class="fas fa-exclamation-triangle"></i>';
                            tooltipText = 'Optimization process is still running';
                            break;
                        case 'Red':
                            icon = '<i class="fas fa-times-circle"></i>';
                            tooltipText = 'An error occurred which the engine could not recover from';
                            break;
                        case 'Grey':
                            icon = '<i class="fas fa-pause-circle"></i>';
                            tooltipText = 'Optimizations are pending after restart';
                            break;
                        default:
                            icon = '';
                            tooltipText = `Status: ${collection.status}`;
                    }
                    
                    statusSpan.innerHTML = `${icon} ${collection.status}`;
                    statusSpan.setAttribute('data-tooltip', tooltipText);
                    topRow.appendChild(statusSpan);
                }
                
                // Size span
                const sizeSpan = document.createElement('span');
                sizeSpan.className = 'collection-size';
                sizeSpan.textContent = this.formatSize(collectionTotalSize);
                topRow.appendChild(sizeSpan);
                
                infoContainer.appendChild(topRow);
                
                // Bottom row: Shards count (if any)
                if (collectionTotalShards > 0) {
                    const shardsSpan = document.createElement('span');
                    shardsSpan.className = 'collection-shards-count';
                    shardsSpan.style.fontSize = '0.8rem';
                    shardsSpan.style.color = '#666';
                    shardsSpan.innerHTML = `<i class="fas fa-layer-group"></i> ${collectionTotalShards} ${collectionTotalShards === 1 ? 'shard' : 'shards'}`;
                    shardsSpan.title = `Unique shards in collection: ${collectionTotalShards}`;
                    infoContainer.appendChild(shardsSpan);
                }
                
                // Create collection actions menu container (three dots menu)
                const collectionActionsMenuContainer = document.createElement('div');
                collectionActionsMenuContainer.className = 'collection-actions-menu-container';
                
                const collectionActionsMenuButton = document.createElement('button');
                collectionActionsMenuButton.className = 'collection-actions-menu-button';
                collectionActionsMenuButton.innerHTML = '<i class="fas fa-ellipsis-v"></i>';
                collectionActionsMenuButton.setAttribute('aria-label', 'Collection actions');
                
                const collectionActionsDropdown = document.createElement('div');
                collectionActionsDropdown.className = 'collection-actions-dropdown';
                
                // Delete (API) action
                const deleteApiAction = document.createElement('button');
                deleteApiAction.className = 'collection-action-item collection-action-item-danger';
                deleteApiAction.innerHTML = '<i class="fas fa-trash"></i> Delete (API)';
                deleteApiAction.title = 'Delete collection via API on selected nodes';
                deleteApiAction.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    collectionActionsDropdown.classList.remove('show');
                    collectionActionsMenuButton.classList.remove('active');
                    this.openCollectionMenus.delete(collection.name);
                    await this.showNodeSelectionDialog(collection, 'deleteApi');
                });
                
                // Delete (Disk) action
                const deleteDiskAction = document.createElement('button');
                deleteDiskAction.className = 'collection-action-item collection-action-item-danger';
                deleteDiskAction.innerHTML = '<i class="fas fa-hdd"></i> Delete (Disk)';
                deleteDiskAction.title = 'Delete collection from disk on selected nodes';
                deleteDiskAction.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    collectionActionsDropdown.classList.remove('show');
                    collectionActionsMenuButton.classList.remove('active');
                    this.openCollectionMenus.delete(collection.name);
                    await this.showNodeSelectionDialog(collection, 'deleteDisk');
                });
                
                // Create Snapshot action
                const createSnapshotAction = document.createElement('button');
                createSnapshotAction.className = 'collection-action-item';
                createSnapshotAction.innerHTML = '<i class="fas fa-camera"></i> Create Snapshot';
                createSnapshotAction.title = 'Create snapshot on selected nodes';
                createSnapshotAction.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    collectionActionsDropdown.classList.remove('show');
                    collectionActionsMenuButton.classList.remove('active');
                    this.openCollectionMenus.delete(collection.name);
                    await this.showNodeSelectionDialog(collection, 'createSnapshot');
                });
                collectionActionsDropdown.appendChild(createSnapshotAction);

                // Manage Aliases action
                const manageAliasesAction = document.createElement('button');
                manageAliasesAction.className = 'collection-action-item';
                manageAliasesAction.innerHTML = '<i class="fas fa-tags"></i> Manage Aliases';
                manageAliasesAction.title = 'Add, rename or delete collection aliases';
                manageAliasesAction.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    collectionActionsDropdown.classList.remove('show');
                    collectionActionsMenuButton.classList.remove('active');
                    this.openCollectionMenus.delete(collection.name);
                    this.showManageAliasesModal(collection);
                });
                collectionActionsDropdown.appendChild(manageAliasesAction);

                // Restore replication factor action
                const restoreRfAction = document.createElement('button');
                restoreRfAction.className = 'collection-action-item';
                restoreRfAction.innerHTML = '<i class="fas fa-sync-alt"></i> Restore replication factor';
                restoreRfAction.title = 'Start background job to restore replication factor for this collection';
                restoreRfAction.addEventListener('click', async (e) => {
                    e.stopPropagation();
                    collectionActionsDropdown.classList.remove('show');
                    collectionActionsMenuButton.classList.remove('active');
                    this.openCollectionMenus.delete(collection.name);
                    await this.startRestoreReplicationFactor(collection.name);
                });
                collectionActionsDropdown.appendChild(restoreRfAction);

                // Delete actions should go last in menu
                collectionActionsDropdown.appendChild(deleteApiAction);
                collectionActionsDropdown.appendChild(deleteDiskAction);

                collectionActionsMenuContainer.appendChild(collectionActionsMenuButton);
                collectionActionsMenuContainer.appendChild(collectionActionsDropdown);
                
                // Add click handler to the menu button
                collectionActionsMenuButton.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const wasOpen = collectionActionsMenuButton.classList.contains('active');
                    // Close all other open collection menus
                    document.querySelectorAll('.collection-actions-menu-button.active').forEach(btn => {
                        btn.classList.remove('active');
                        const container = btn.parentElement;
                        const menu = container?.querySelector('.collection-actions-dropdown');
                        if (menu) {
                            menu.classList.remove('show');
                        }
                        // Update state for closed menus
                        const row = btn.closest('.collection-row');
                        if (row) {
                            const nameCell = row.querySelector('.collection-name-line');
                            if (nameCell) {
                                const collectionName = nameCell.textContent?.trim();
                                if (collectionName) {
                                    this.openCollectionMenus.delete(collectionName);
                                }
                            }
                        }
                    });
                    
                    if (!wasOpen) {
                        collectionActionsMenuButton.classList.add('active');
                        collectionActionsDropdown.classList.add('show');
                        // Update state for opened menu
                        this.openCollectionMenus.add(collection.name);
                    }
                });
                
                // Close dropdown when clicking outside
                document.addEventListener('click', (e) => {
                    if (!collectionActionsMenuContainer.contains(e.target)) {
                        collectionActionsDropdown.classList.remove('show');
                        collectionActionsMenuButton.classList.remove('active');
                        // Update state when closed by outside click
                        this.openCollectionMenus.delete(collection.name);
                    }
                });
                
                // Restore menu state if it was open before refresh
                if (this.openCollectionMenus.has(collection.name)) {
                    collectionActionsMenuButton.classList.add('active');
                    collectionActionsDropdown.classList.add('show');
                    console.log('Restored open menu state for collection:', collection.name);
                }
                
                // Add menu button to the top row (after size)
                topRow.appendChild(collectionActionsMenuContainer);
                
                headerContainer.appendChild(nameContainer);
                headerContainer.appendChild(infoContainer);
                nameCell.appendChild(headerContainer);
                row.appendChild(nameCell);

                const detailsRow = document.createElement('tr');
                detailsRow.className = 'collection-details';
                const shouldBeVisible = this.openCollections.has(collection.name);
                if (shouldBeVisible) {
                    detailsRow.classList.add('visible');
                    console.log(`Restoring visible state for collection: ${collection.name}`);
                }

                const detailsCell = document.createElement('td');
                detailsCell.colSpan = nodeKeys.length + 1;
                const detailsContent = document.createElement('div');
                detailsContent.className = 'collection-details-content';

                // Iterate over nodes in backend order (already preserved in array)
                collection.nodes.forEach(nodeInfo => {
                    const nodeDetails = document.createElement('div');
                    nodeDetails.className = 'collection-node-info';
                    
                    const peerIdDisplay = nodeInfo.peerId ? ` <span class="node-peer-id">(${nodeInfo.peerId})</span>` : '';
                        const stateKey = `${collection.name}-${nodeInfo.peerId}`;
                        
                        // Get shards HTML (includes Target nodes, Shards, and action controls)
                        const shardsHtml = nodeInfo.metrics.shards ? 
                            this.formatMetricValue('shards', nodeInfo.metrics.shards, nodeInfo) : '';
                        
                        // Get transfers HTML
                        let transfersHtml = '';
                        if (nodeInfo.metrics.outgoingTransfers) {
                            const transfersValue = this.formatMetricValue('outgoingTransfers', nodeInfo.metrics.outgoingTransfers, nodeInfo);
                            if (transfersValue) {
                                transfersHtml = `
                                    <div class="transfers-section">
                                        <dt>
                                            Transfers:
                                            <i class="fas fa-info-circle" 
                                               style="color: #2196f3; font-size: 0.8em; margin-left: 4px; cursor: help;" 
                                               title="Active shard transfers. Note: Transfers may complete quickly. If abort fails, the transfer has likely already finished."></i>
                                        </dt>
                                        <dd>${transfersValue}</dd>
                                    </div>
                                `;
                            }
                        }
                        
                        // 4. Get other metrics (if any)
                        const otherMetricsHtml = Object.entries(nodeInfo.metrics)
                            .filter(([key]) => key !== 'prettySize' && key !== 'sizeBytes' && key !== 'shardStates' && 
                                              key !== 'shard_states' && key !== 'shards' && key !== 'outgoingTransfers')
                            .map(([key, value]) => {
                                const formattedValue = this.formatMetricValue(key, value, nodeInfo);
                                if (!formattedValue) return '';
                                
                                const formattedKey = key.charAt(0).toUpperCase() + key.slice(1);
                                return `
                                    <dt>${formattedKey}:</dt>
                                    <dd>${formattedValue}</dd>
                                `;
                            })
                            .filter(html => html)
                            .join('');
                            
                        // Determine display name: use podName if available and not 'unknown', otherwise use peerId
                        const displayName = nodeInfo.podName && nodeInfo.podName !== 'unknown' 
                            ? nodeInfo.podName 
                            : (nodeInfo.peerId || nodeInfo.nodeUrl);
                        const fullNodeTitle = nodeInfo.peerId ? `${displayName} (${nodeInfo.peerId})` : displayName;
                        
                        // Format size for header line
                        let sizeForHeader = '';
                        if (nodeInfo.metrics.sizeBytes) {
                            const formattedSize = this.formatSize(nodeInfo.metrics.sizeBytes);
                            sizeForHeader = `<span class="node-size-badge">${formattedSize}</span>`;
                        }
                        
                        nodeDetails.innerHTML = `
                            <div class="node-info-header">
                                <h4 title="${fullNodeTitle}">${displayName}${peerIdDisplay}</h4>
                            </div>
                            <div class="node-size-line">
                                ${sizeForHeader}
                            </div>
                            ${shardsHtml}
                            ${otherMetricsHtml ? `<dl class="other-metrics">${otherMetricsHtml}</dl>` : ''}
                            ${transfersHtml ? `<dl class="transfers-metrics">${transfersHtml}</dl>` : ''}
                        `;

                        nodeDetails.setAttribute('data-state-key', stateKey);
                        
                        // Restore shard selection state
                        const savedState = this.selectedState.get(stateKey) || { selectedShards: new Set() };
                        console.log(`Restoring shard state for ${stateKey}:`, Array.from(savedState.selectedShards));
                        
                        // Setup Select All shards checkbox
                        const selectAllCheckbox = nodeDetails.querySelector('.select-all-shards-checkbox');
                        if (selectAllCheckbox) {
                            const shardCheckboxes = nodeDetails.querySelectorAll('.shard-checkbox');
                            
                            // Restore previously selected shards
                            shardCheckboxes.forEach(cb => {
                                const shardId = parseInt(cb.dataset.shardId);
                                if (savedState.selectedShards.has(shardId)) {
                                    cb.checked = true;
                                }
                            });
                            
                            // Update Select All state based on individual checkboxes
                            const updateSelectAllState = () => {
                                const checkedCount = Array.from(shardCheckboxes).filter(cb => cb.checked).length;
                                selectAllCheckbox.checked = checkedCount === shardCheckboxes.length && shardCheckboxes.length > 0;
                                selectAllCheckbox.indeterminate = checkedCount > 0 && checkedCount < shardCheckboxes.length;
                            };
                            
                            // Handle Select All change
                            selectAllCheckbox.addEventListener('change', () => {
                                // Clear selection on other nodes
                                this.clearOtherNodesShardSelection(stateKey);
                                
                                shardCheckboxes.forEach(cb => {
                                    cb.checked = selectAllCheckbox.checked;
                                });
                                this.saveShardSelection(stateKey, shardCheckboxes);
                                updateSelectAllState();
                            });
                            
                            // Update Select All when individual checkboxes change
                            shardCheckboxes.forEach(cb => {
                                cb.addEventListener('change', () => {
                                    // Clear selection on other nodes when first shard is selected
                                    const anyChecked = Array.from(shardCheckboxes).some(checkbox => checkbox.checked);
                                    if (anyChecked) {
                                        this.clearOtherNodesShardSelection(stateKey);
                                    }
                                    
                                    this.saveShardSelection(stateKey, shardCheckboxes);
                                    updateSelectAllState();
                                });
                            });
                            
                            // Initial state
                        updateSelectAllState();
                    }

                    // Setup Abort Transfer buttons
                    const abortButtons = nodeDetails.querySelectorAll('.abort-transfer-button');
                    abortButtons.forEach(button => {
                        button.addEventListener('click', async (e) => {
                            e.stopPropagation();
                            const shardId = parseInt(button.dataset.shardId);
                            // Parse as number - peer IDs can be large numbers
                            const sourcePeerId = Number(button.dataset.sourcePeer);
                            const targetPeerId = Number(button.dataset.targetPeer);
                            
                            console.log('Abort transfer request:', {
                                collectionName: collection.name,
                                shardId,
                                sourcePeerId,
                                targetPeerId,
                                sourcePeerType: typeof sourcePeerId,
                                targetPeerType: typeof targetPeerId
                            });
                            
                            if (!confirm(`Are you sure you want to abort the transfer of shard ${shardId}?\n\nFrom: peer ${sourcePeerId}\nTo: peer ${targetPeerId}\n\nNote: If the transfer has already completed, this operation will fail.`)) {
                                return;
                            }
                            
                            button.disabled = true;
                            button.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Aborting...';
                            
                            try {
                                const requestBody = {
                                    collectionName: collection.name,
                                    sourcePeerId: sourcePeerId,
                                    targetPeerId: targetPeerId,
                                    shardId: shardId
                                };
                                
                                console.log('Sending abort transfer request:', requestBody);
                                
                                const response = await fetch('/api/v1/cluster/abort-shard-transfer', {
                                    method: 'POST',
                                    headers: {
                                        'Content-Type': 'application/json'
                                    },
                                    body: JSON.stringify(requestBody)
                                });
                                
                                const result = await response.json();
                                
                                if (response.ok) {
                                    this.showToast(`Shard transfer aborted successfully`, 'success', 'Success', 3000);
                                    // Refresh to update the UI
                                    setTimeout(() => this.refresh(), 1000);
                                } else {
                                    const errorMsg = result.error || result.details || 'Failed to abort shard transfer';
                                    // Check if error is about transfer not existing
                                    if (errorMsg.includes('completed') || errorMsg.includes('cancelled') || 
                                        (result.details && result.details.includes('completed'))) {
                                        this.showToast('Transfer not found - it may have already completed or been cancelled. Refreshing...', 'info', 'Transfer Completed', 4000);
                                        setTimeout(() => this.refresh(), 1500);
                                        return; // Don't throw error, just refresh
                                    }
                                    throw new Error(errorMsg);
                                }
                            } catch (error) {
                                console.error('Error aborting shard transfer:', error);
                                this.showToast(`Failed to abort transfer: ${error.message}`, 'error', 'Error', 5000);
                                button.disabled = false;
                                button.innerHTML = '<i class="fas fa-stop-circle"></i> Abort';
                            }
                        });
                    });

                    detailsContent.appendChild(nodeDetails);
                });

                // Add collection-level action buttons at the bottom
                const actionsFooter = document.createElement('div');
                actionsFooter.className = 'collection-actions-footer';
                actionsFooter.style.justifyContent = 'space-between';
                actionsFooter.style.alignItems = 'center';
                
                // Left side: Collection info or placeholder (buttons moved to dropdown menu in header)
                const collectionActionsContainer = document.createElement('div');
                collectionActionsContainer.style.cssText = `
                    display: flex;
                    gap: 8px;
                    align-items: center;
                `;
                
                // Note: Delete (API), Delete (Disk), and Create Snapshot buttons are now in the dropdown menu
                
                // Right side: Shard actions (Sync/Drop) - initially hidden
                const shardActionsContainer = document.createElement('div');
                shardActionsContainer.className = 'shard-actions-container';
                shardActionsContainer.style.cssText = `
                    display: none;
                    gap: 8px;
                    align-items: center;
                `;
                
                actionsFooter.appendChild(collectionActionsContainer);
                actionsFooter.appendChild(shardActionsContainer);
                
                // Function to update shard action buttons
                const updateShardActionButtons = () => {
                    // Find all checked shard checkboxes in this collection
                    const allShardCheckboxes = detailsContent.querySelectorAll('.shard-checkbox:checked');
                    const selectedShards = Array.from(allShardCheckboxes).map(cb => ({
                        shardId: parseInt(cb.dataset.shardId),
                        nodeElement: cb.closest('.collection-node-info')
                    }));
                    
                    if (selectedShards.length > 0) {
                        // Get node info from the first selected shard's parent node
                        let nodeInfo = null;
                        const firstNodeElement = selectedShards[0].nodeElement;
                        const allNodesInDetails = detailsContent.querySelectorAll('.collection-node-info');
                        const nodeIndex = Array.from(allNodesInDetails).indexOf(firstNodeElement);
                        if (nodeIndex !== -1 && nodeIndex < collection.nodes.length) {
                            nodeInfo = collection.nodes[nodeIndex];
                        }
                        
                        if (!nodeInfo && collection.nodes.length > 0) {
                            nodeInfo = collection.nodes[0];
                        }
                        
                        const shardIds = selectedShards.map(s => s.shardId);
                        
                        // Show container and recreate buttons
                        shardActionsContainer.style.display = 'flex';
                        shardActionsContainer.innerHTML = '';
                        
                        // Add info text
                        const infoText = document.createElement('span');
                        infoText.style.cssText = 'color: #555; font-size: 0.85rem; font-weight: 500;';
                        infoText.textContent = `${shardIds.length} shard${shardIds.length > 1 ? 's' : ''} selected:`;
                        shardActionsContainer.appendChild(infoText);
                        
                        // Create Sync button
                        const syncButton = document.createElement('button');
                        syncButton.className = 'replicate-button';
                        syncButton.textContent = 'Sync';
                        syncButton.style.margin = '0';
                        syncButton.addEventListener('click', () => {
                            this.showShardSyncModal(collection, nodeInfo, shardIds);
                        });
                        
                        // Create Drop button
                        const dropButton = document.createElement('button');
                        dropButton.className = 'drop-shards-button';
                        dropButton.textContent = 'Drop';
                        dropButton.style.margin = '0';
                        dropButton.addEventListener('click', async () => {
                            // Get peerId
                            const peerId = nodeInfo.peerId;
                            if (!peerId) {
                                alert('Peer ID is not available for this node');
                                return;
                            }

                            const nodeDisplay = nodeInfo.podName && nodeInfo.podName !== 'unknown' 
                                ? nodeInfo.podName 
                                : nodeInfo.peerId;

                            if (!confirm(`Are you sure you want to drop shards [${shardIds.join(', ')}] from node ${nodeDisplay}?\n\nThis action cannot be undone!`)) {
                                return;
                            }

                            try {
                                const requestBody = {
                                    collectionName: collection.name,
                                    peerId: parseInt(peerId),
                                    shardIds: shardIds,
                                    isDryRun: false
                                };

                                const response = await fetch(this.dropShardsEndpoint, {
                                    method: 'POST',
                                    headers: {
                                        'Content-Type': 'application/json',
                                    },
                                    body: JSON.stringify(requestBody)
                                });

                                if (!response.ok) {
                                    const error = await response.json();
                                    throw new Error(error.details || 'Failed to drop shards');
                                }

                                this.showToast(`Shards [${shardIds.join(', ')}] dropped successfully from ${nodeDisplay}`, 'success', 'Drop Completed', 5000);
                                setTimeout(() => this.refresh(), 2000);
                            } catch (error) {
                                this.showToast(`Error: ${error.message}`, 'error', 'Drop Failed', 10000);
                            }
                        });
                        
                        shardActionsContainer.appendChild(syncButton);
                        shardActionsContainer.appendChild(dropButton);
                    } else {
                        // Hide container when no shards selected
                        shardActionsContainer.style.display = 'none';
                        shardActionsContainer.innerHTML = '';
                    }
                };
                
                // Add event listeners to all checkboxes to update shard action buttons
                const allCheckboxes = detailsContent.querySelectorAll('.shard-checkbox, .select-all-shards-checkbox');
                allCheckboxes.forEach(cb => {
                    cb.addEventListener('change', updateShardActionButtons);
                });
                
                // Initial update
                updateShardActionButtons();
                
                detailsContent.appendChild(actionsFooter);

                detailsCell.appendChild(detailsContent);
                detailsRow.appendChild(detailsCell);

                row.addEventListener('click', () => {
                    const wasVisible = detailsRow.classList.contains('visible');
                    console.log(`Collection ${collection.name} clicked, was visible: ${wasVisible}, will be: ${!wasVisible}`);
                    if (wasVisible) {
                        detailsRow.classList.remove('visible');
                        this.openCollections.delete(collection.name);
                    } else {
                        detailsRow.classList.add('visible');
                        this.openCollections.add(collection.name);
                    }
                });

                tbody.appendChild(row);
                tbody.appendChild(detailsRow);
            });

        table.appendChild(tbody);
        const container = document.getElementById('collectionsTable');
        container.innerHTML = '';
        container.appendChild(table);
    }

    async loadSnapshots(clearCache = false) {
        const controller = new AbortController();
        const timeoutId = setTimeout(() => controller.abort(), 30000); // 30 second timeout
        
        try {
            const params = new URLSearchParams({
                page: this.snapshotCurrentPage.toString(),
                pageSize: this.snapshotPageSize.toString(),
                clearCache: clearCache.toString()
            });

            if (this.snapshotNameFilter) {
                params.append('nameFilter', this.snapshotNameFilter);
            }

            const response = await fetch(`${this.snapshotsApiEndpoint}?${params}`, {
                signal: controller.signal
            });
            clearTimeout(timeoutId);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            
            // Update pagination state
            if (data.pagination) {
                this.snapshotTotalPages = data.pagination.totalPages;
                this.updateSnapshotPaginationControls();
                
                // Update total counts
                const totalCount = data.pagination.totalItems;
                document.getElementById('totalSnapshotsCount').textContent = `Collections: ${totalCount}`;
            }
            
            // Use groupedSnapshots from backend
            const groupedSnapshots = data.groupedSnapshots || [];
            this.updateSnapshotsTable(groupedSnapshots);
            
        } catch (error) {
            clearTimeout(timeoutId);
            console.error('Error fetching snapshots:', error);
            
            // Show error as toast notification
            let errorMessage;
            if (error.name === 'AbortError') {
                errorMessage = 'Snapshots request timed out after 30 seconds. Please check your connection.';
            } else {
                errorMessage = this.getErrorMessage(error);
            }
            this.showToast(`Error loading snapshots: ${errorMessage}`, 'error', 'Snapshot Load Error', 15000);
        }
    }

    updateSnapshotsTable(groupedSnapshots) {
        if (!groupedSnapshots || groupedSnapshots.length === 0) {
            const container = document.getElementById('snapshotsTable');
            container.innerHTML = '<p style="color: #999; padding: 20px; text-align: center;">No snapshots found</p>';
            document.getElementById('totalSnapshotsSize').textContent = 'Total Size: 0 B';
            return;
        }

        // Calculate total size from all groups
        const totalSize = groupedSnapshots.reduce((sum, group) => sum + group.totalSize, 0);

        // Update total size display
        document.getElementById('totalSnapshotsSize').textContent = `Total Size: ${this.formatSize(totalSize)}`;

        // Create table
        const table = document.createElement('table');
        table.className = 'collections-table'; // Use same class as collections for consistent styling
        const tbody = document.createElement('tbody');

        groupedSnapshots.forEach(collection => {
            // Main collection row
            const row = document.createElement('tr');
            row.className = 'collection-row'; // Use same class as collections
            
            const key = collection.collectionName;
            const isOpen = this.openSnapshots.has(key);

            const nameCell = document.createElement('td');
            nameCell.className = 'collection-name'; // Use same class as collections
            nameCell.colSpan = 1;
            
            const headerContainer = document.createElement('div');
            headerContainer.className = 'collection-header-container';
            headerContainer.style.display = 'flex';
            headerContainer.style.justifyContent = 'space-between';
            headerContainer.style.alignItems = 'center';
            
            const nameDiv = document.createElement('div');
            nameDiv.className = 'collection-name-line';
            nameDiv.style.display = 'flex';
            nameDiv.style.alignItems = 'center';
            nameDiv.style.gap = '8px';
            
            const nameContent = document.createElement('span');
            nameContent.innerHTML = `<i class="fas fa-camera" style="color: #7b1fa2; margin-right: 8px;"></i>${collection.collectionName}`;
            nameContent.title = collection.collectionName;
            nameDiv.appendChild(nameContent);
            
            // Copy to clipboard button
            const copyButton = document.createElement('button');
            copyButton.className = 'collection-name-copy-btn';
            copyButton.innerHTML = '<i class="fas fa-copy"></i>';
            copyButton.title = 'Copy collection name to clipboard';
            copyButton.addEventListener('click', (e) => {
                e.stopPropagation(); // Prevent row click
                
                // Try modern clipboard API first, fallback for HTTP contexts
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    navigator.clipboard.writeText(collection.collectionName).then(() => {
                        this.showToast(`Collection name "${collection.collectionName}" copied to clipboard`, 'success', 'Copied', 2000);
                    }).catch(err => {
                        console.error('Failed to copy:', err);
                        this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                    });
                } else {
                    // Fallback for HTTP contexts where Clipboard API is not available
                    try {
                        const textArea = document.createElement('textarea');
                        textArea.value = collection.collectionName;
                        textArea.style.position = 'fixed';
                        textArea.style.left = '-999999px';
                        textArea.style.top = '-999999px';
                        document.body.appendChild(textArea);
                        textArea.focus();
                        textArea.select();
                        const successful = document.execCommand('copy');
                        document.body.removeChild(textArea);
                        
                        if (successful) {
                            this.showToast(`Collection name "${collection.collectionName}" copied to clipboard`, 'success', 'Copied', 2000);
                        } else {
                            this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                        }
                    } catch (err) {
                        console.error('Fallback: Could not copy text', err);
                        this.showToast('Failed to copy to clipboard', 'error', 'Error', 3000);
                    }
                }
            });
            nameDiv.appendChild(copyButton);
            
            const rightContainer = document.createElement('div');
            rightContainer.style.display = 'flex';
            rightContainer.style.alignItems = 'center';
            rightContainer.style.gap = '8px';
            
            const sizeSpan = document.createElement('span');
            sizeSpan.className = 'collection-size';
            sizeSpan.textContent = this.formatSize(collection.totalSize);
            rightContainer.appendChild(sizeSpan);
            
            // Create snapshot collection actions menu (three dots)
            const snapshotCollectionMenuContainer = document.createElement('div');
            snapshotCollectionMenuContainer.className = 'snapshot-collection-menu-container';
            
            const snapshotCollectionMenuButton = document.createElement('button');
            snapshotCollectionMenuButton.className = 'snapshot-collection-menu-button';
            snapshotCollectionMenuButton.innerHTML = '<i class="fas fa-ellipsis-v"></i>';
            snapshotCollectionMenuButton.setAttribute('aria-label', 'Snapshot collection actions');
            
            const snapshotCollectionDropdown = document.createElement('div');
            snapshotCollectionDropdown.className = 'snapshot-collection-dropdown';
            
            // Delete All Snapshots action
            const deleteAllAction = document.createElement('button');
            deleteAllAction.className = 'snapshot-collection-action-item snapshot-collection-action-item-danger';
            deleteAllAction.innerHTML = '<i class="fas fa-trash"></i> Delete All Snapshots';
            deleteAllAction.title = 'Delete all snapshots for this collection from all nodes';
            deleteAllAction.addEventListener('click', (e) => {
                e.stopPropagation();
                snapshotCollectionDropdown.classList.remove('show');
                snapshotCollectionMenuButton.classList.remove('active');
                this.openSnapshotCollectionMenus.delete(collection.collectionName);
                this.deleteSnapshotFromAllNodes(collection);
            });
            snapshotCollectionDropdown.appendChild(deleteAllAction);
            
            snapshotCollectionMenuContainer.appendChild(snapshotCollectionMenuButton);
            snapshotCollectionMenuContainer.appendChild(snapshotCollectionDropdown);
            
            // Add click handler to the menu button
            snapshotCollectionMenuButton.addEventListener('click', (e) => {
                e.stopPropagation();
                const wasOpen = snapshotCollectionMenuButton.classList.contains('active');
                // Close all other open snapshot collection menus
                document.querySelectorAll('.snapshot-collection-menu-button.active').forEach(btn => {
                    btn.classList.remove('active');
                    const container = btn.parentElement;
                    const menu = container?.querySelector('.snapshot-collection-dropdown');
                    if (menu) {
                        menu.classList.remove('show');
                    }
                    // Remove from tracking when closing other menus
                    const parentRow = btn.closest('tr');
                    if (parentRow) {
                        const collectionNameEl = parentRow.querySelector('.collection-name-line span');
                        if (collectionNameEl) {
                            const collectionName = collectionNameEl.textContent?.replace(/^\s*\S+\s*/, '').trim(); // Remove icon
                            if (collectionName) {
                                this.openSnapshotCollectionMenus.delete(collectionName);
                            }
                        }
                    }
                });
                
                if (!wasOpen) {
                    snapshotCollectionMenuButton.classList.add('active');
                    snapshotCollectionDropdown.classList.add('show');
                    this.openSnapshotCollectionMenus.add(collection.collectionName);
                } else {
                    this.openSnapshotCollectionMenus.delete(collection.collectionName);
                }
            });
            
            // Close dropdown when clicking outside
            document.addEventListener('click', (e) => {
                if (!snapshotCollectionMenuContainer.contains(e.target)) {
                    snapshotCollectionDropdown.classList.remove('show');
                    snapshotCollectionMenuButton.classList.remove('active');
                    this.openSnapshotCollectionMenus.delete(collection.collectionName);
                }
            });
            
            // Restore menu state if it was open before refresh
            if (this.openSnapshotCollectionMenus.has(collection.collectionName)) {
                snapshotCollectionMenuButton.classList.add('active');
                snapshotCollectionDropdown.classList.add('show');
            }
            
            rightContainer.appendChild(snapshotCollectionMenuContainer);
            
            headerContainer.appendChild(nameDiv);
            headerContainer.appendChild(rightContainer);
            nameCell.appendChild(headerContainer);
            row.appendChild(nameCell);

            // Details row for nodes
            const detailsRow = document.createElement('tr');
            detailsRow.className = `collection-details ${isOpen ? 'visible' : ''}`;
            const detailsCell = document.createElement('td');
            detailsCell.colSpan = 1;
            const detailsContent = document.createElement('div');
            detailsContent.className = 'collection-details-content';
            
            const nodesTable = document.createElement('table');
            nodesTable.className = 'nodes-table';
            
            const nodesHeader = document.createElement('tr');
            nodesHeader.innerHTML = `
                <th>Node</th>
                <th>Peer ID</th>
                <th>Pod</th>
                <th>Snapshot Name</th>
                <th>Size</th>
            `;
            nodesTable.appendChild(nodesHeader);

            collection.snapshots.forEach((snapshot, index) => {
                // Create unique key for this snapshot (collection + snapshot name + node)
                const snapshotKey = `${collection.collectionName}|${snapshot.snapshotName}|${snapshot.nodeUrl}`;
                
                const nodeRow = document.createElement('tr');
                nodeRow.className = 'snapshot-table-row';
                nodeRow.setAttribute('data-snapshot-index', index);
                
                // Create cells
                const cellNode = document.createElement('td');
                cellNode.textContent = snapshot.nodeUrl;
                
                const cellPeer = document.createElement('td');
                cellPeer.innerHTML = `<code>${snapshot.peerId}</code>`;
                
                const cellPod = document.createElement('td');
                cellPod.textContent = snapshot.podName;
                
                const cellSnapshot = document.createElement('td');
                cellSnapshot.textContent = snapshot.snapshotName;
                
                const cellSize = document.createElement('td');
                cellSize.style.position = 'relative';
                
                // Create container for size and menu button
                const sizeContainer = document.createElement('div');
                sizeContainer.style.display = 'flex';
                sizeContainer.style.alignItems = 'center';
                sizeContainer.style.justifyContent = 'space-between';
                sizeContainer.style.gap = '8px';
                
                const sizeSpan = document.createElement('span');
                sizeSpan.textContent = snapshot.prettySize;
                sizeContainer.appendChild(sizeSpan);
                
                // Create snapshot actions menu (three dots) in Size cell
                const snapshotActionsMenuContainer = document.createElement('div');
                snapshotActionsMenuContainer.className = 'snapshot-actions-menu-container-inline';
                snapshotActionsMenuContainer.style.flexShrink = '0';
                
                const snapshotActionsMenuButton = document.createElement('button');
                snapshotActionsMenuButton.className = 'snapshot-actions-menu-button';
                snapshotActionsMenuButton.innerHTML = '<i class="fas fa-ellipsis-v"></i>';
                snapshotActionsMenuButton.setAttribute('aria-label', 'Snapshot actions');
                
                const snapshotActionsDropdown = document.createElement('div');
                snapshotActionsDropdown.className = 'snapshot-actions-dropdown';
                
                // Download action
                const downloadAction = document.createElement('button');
                downloadAction.className = 'snapshot-action-item';
                downloadAction.innerHTML = '<i class="fas fa-download"></i> Download';
                downloadAction.title = 'Download snapshot (tries API first, then disk fallback)';
                downloadAction.addEventListener('click', (e) => {
                    e.stopPropagation();
                    snapshotActionsDropdown.classList.remove('show');
                    snapshotActionsMenuButton.classList.remove('active');
                    this.openSnapshotMenus.delete(snapshotKey);
                    this.downloadSnapshot(
                        collection.collectionName, 
                        snapshot.snapshotName, 
                        snapshot.nodeUrl,
                        snapshot.podName, 
                        snapshot.podNamespace || 'qdrant',
                        snapshot.source
                    );
                });
                snapshotActionsDropdown.appendChild(downloadAction);
                
                // Get Download URL action (only for S3 snapshots)
                if (snapshot.source === 'S3Storage') {
                    const getUrlAction = document.createElement('button');
                    getUrlAction.className = 'snapshot-action-item';
                    getUrlAction.innerHTML = '<i class="fas fa-link"></i> Get Download URL';
                    getUrlAction.title = 'Get presigned download URL (valid for 1 hour)';
                    getUrlAction.addEventListener('click', (e) => {
                        e.stopPropagation();
                        snapshotActionsDropdown.classList.remove('show');
                        snapshotActionsMenuButton.classList.remove('active');
                        this.openSnapshotMenus.delete(snapshotKey);
                        this.getS3DownloadUrl(collection.collectionName, snapshot.snapshotName);
                    });
                    snapshotActionsDropdown.appendChild(getUrlAction);
                }
                
                // Recover action
                const recoverAction = document.createElement('button');
                recoverAction.className = 'snapshot-action-item';
                recoverAction.innerHTML = '<i class="fas fa-undo"></i> Recover';
                recoverAction.title = 'Recover from this snapshot';
                recoverAction.addEventListener('click', (e) => {
                    e.stopPropagation();
                    snapshotActionsDropdown.classList.remove('show');
                    snapshotActionsMenuButton.classList.remove('active');
                    this.openSnapshotMenus.delete(snapshotKey);
                    this.openRecoveryModal(snapshot, collection.collectionName, snapshot.snapshotName);
                });
                snapshotActionsDropdown.appendChild(recoverAction);
                
                // Delete action
                const deleteAction = document.createElement('button');
                deleteAction.className = 'snapshot-action-item snapshot-action-item-danger';
                deleteAction.innerHTML = '<i class="fas fa-trash"></i> Delete';
                deleteAction.title = 'Delete this snapshot';
                deleteAction.addEventListener('click', (e) => {
                    e.stopPropagation();
                    snapshotActionsDropdown.classList.remove('show');
                    snapshotActionsMenuButton.classList.remove('active');
                    this.openSnapshotMenus.delete(snapshotKey);
                    this.deleteSnapshotFromNode(snapshot);
                });
                snapshotActionsDropdown.appendChild(deleteAction);
                
                snapshotActionsMenuContainer.appendChild(snapshotActionsMenuButton);
                snapshotActionsMenuContainer.appendChild(snapshotActionsDropdown);
                
                // Add click handler to the menu button
                snapshotActionsMenuButton.addEventListener('click', (e) => {
                    e.stopPropagation();
                    const wasOpen = snapshotActionsMenuButton.classList.contains('active');
                    // Close all other open snapshot action menus
                    document.querySelectorAll('.snapshot-actions-menu-button.active').forEach(btn => {
                        btn.classList.remove('active');
                        const container = btn.parentElement;
                        const menu = container?.querySelector('.snapshot-actions-dropdown');
                        if (menu) {
                            menu.classList.remove('show');
                        }
                    });
                    
                    // Clear all tracked open menus when closing others
                    this.openSnapshotMenus.clear();
                    
                    if (!wasOpen) {
                        snapshotActionsMenuButton.classList.add('active');
                        snapshotActionsDropdown.classList.add('show');
                        this.openSnapshotMenus.add(snapshotKey);
                    }
                });
                
                // Close dropdown when clicking outside
                document.addEventListener('click', (e) => {
                    if (!snapshotActionsMenuContainer.contains(e.target)) {
                        snapshotActionsDropdown.classList.remove('show');
                        snapshotActionsMenuButton.classList.remove('active');
                        this.openSnapshotMenus.delete(snapshotKey);
                    }
                });
                
                // Restore menu state if it was open before refresh
                if (this.openSnapshotMenus.has(snapshotKey)) {
                    snapshotActionsMenuButton.classList.add('active');
                    snapshotActionsDropdown.classList.add('show');
                }
                
                sizeContainer.appendChild(snapshotActionsMenuContainer);
                cellSize.appendChild(sizeContainer);
                
                nodeRow.appendChild(cellNode);
                nodeRow.appendChild(cellPeer);
                nodeRow.appendChild(cellPod);
                nodeRow.appendChild(cellSnapshot);
                nodeRow.appendChild(cellSize);
                
                nodesTable.appendChild(nodeRow);
            });


            detailsContent.appendChild(nodesTable);
            detailsCell.appendChild(detailsContent);
            detailsRow.appendChild(detailsCell);

            // Toggle details on click
            row.addEventListener('click', () => {
                if (this.openSnapshots.has(key)) {
                    this.openSnapshots.delete(key);
                    detailsRow.classList.remove('visible');
                } else {
                    this.openSnapshots.add(key);
                    detailsRow.classList.add('visible');
                }
            });

            tbody.appendChild(row);
            tbody.appendChild(detailsRow);
        });

        table.appendChild(tbody);
        const container = document.getElementById('snapshotsTable');
        container.innerHTML = '';
        container.appendChild(table);
    }

    async recoverSnapshotFromNode(nodeUrl, collectionName, snapshotName, podName = null, source = 'QdrantApi', sourceCollectionName = null, snapshotPriority = null, waitForResult = true) {
        // Show podName if available and not 'unknown', otherwise show nodeUrl
        const nodeIdentifier = podName && podName !== 'unknown' ? podName : nodeUrl;
        const toastId = this.showToast(`Recovering ${collectionName} from ${snapshotName} on ${nodeIdentifier}...`, 'info', null, 0);
        
        const requestBody = {
            TargetNodeUrl: nodeUrl,
            CollectionName: collectionName,
            SnapshotName: snapshotName,
            Source: source,
            WaitForResult: waitForResult
        };

        if (snapshotPriority) {
            requestBody.SnapshotPriority = snapshotPriority;
        }
        
        // Add SourceCollectionName to help locate the file in the correct directory
        // This is important when recovering to a different collection name
        if (sourceCollectionName) {
            requestBody.SourceCollectionName = sourceCollectionName;
        }
        
        console.log('Recovery request body:', requestBody);
        
        try {
            const response = await fetch(this.recoverFromSnapshotEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Recovery response:', { status: response.status, result });
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', null, 5000);
                // Refresh data after successful recovery
                setTimeout(() => this.refresh(), 2000);
            } else {
                // Handle validation errors
                let errorMessage = result.message || 'Recovery failed';
                if (result.errors) {
                    const errorDetails = Object.entries(result.errors)
                        .map(([field, messages]) => `${field}: ${Array.isArray(messages) ? messages.join(', ') : messages}`)
                        .join('\n');
                    errorMessage += `\n\n${errorDetails}`;
                    console.error('Validation errors:', result.errors);
                }
                this.showToast(`✗ ${errorMessage}`, 'error', null, 15000);
            }
        } catch (error) {
            console.error('Recovery error:', error);
            this.removeToast(toastId);
            const errorMessage = this.getErrorMessage(error);
            this.showToast(`✗ Error recovering snapshot: ${errorMessage}`, 'error', null, 15000);
        }
    }


    async deleteSnapshotFromNode(snapshot) {
        // Show podName if available and not 'unknown', otherwise show nodeUrl
        const identifier = snapshot.podName && snapshot.podName !== 'unknown' ? snapshot.podName : snapshot.nodeUrl;
        if (!confirm(`Delete snapshot ${snapshot.snapshotName} for ${snapshot.collectionName} from ${identifier}?`)) {
            return;
        }

        const toastId = this.showToast(`Deleting ${snapshot.snapshotName} from ${identifier}...`, 'info', 0);
        
        try {
            console.log('Snapshot object:', {
                collectionName: snapshot.collectionName,
                snapshotName: snapshot.snapshotName,
                source: snapshot.source,
                nodeUrl: snapshot.nodeUrl,
                nodeUrlType: typeof snapshot.nodeUrl,
                nodeUrlLength: snapshot.nodeUrl?.length,
                podName: snapshot.podName,
                podNamespace: snapshot.podNamespace
            });

            const requestBody = {
                CollectionName: snapshot.collectionName,
                SnapshotName: snapshot.snapshotName,
                Source: snapshot.source
            };

            // API contract:
            // - QdrantApi => NodeUrls: string[]
            // - KubernetesStorage => Pods: [{ PodName, PodNamespace }]
            // - S3Storage => no extra fields
            if (snapshot.source === 'QdrantApi') {
                if (snapshot.nodeUrl && snapshot.nodeUrl.trim() !== '' && snapshot.nodeUrl !== 'S3') {
                    requestBody.NodeUrls = [snapshot.nodeUrl];
                    console.log('Added NodeUrls to request:', requestBody.NodeUrls);
                } else {
                    console.log('NodeUrls NOT added - nodeUrl value:', snapshot.nodeUrl);
                }
            } else if (snapshot.source === 'KubernetesStorage') {
                if (
                    snapshot.podName && snapshot.podName.trim() !== '' && snapshot.podName !== 'S3' && snapshot.podName !== 'unknown' &&
                    snapshot.podNamespace && snapshot.podNamespace.trim() !== '' && snapshot.podNamespace !== 'S3'
                ) {
                    requestBody.Pods = [{
                        PodName: snapshot.podName,
                        PodNamespace: snapshot.podNamespace
                    }];
                    console.log('Added Pods to request:', requestBody.Pods);
                } else {
                    console.log('Pods NOT added - pod values:', snapshot.podName, snapshot.podNamespace);
                }
            } else {
                console.log('S3Storage snapshot - no NodeUrls/Pods required');
            }

            console.log('Delete snapshot request:', requestBody);

            const response = await fetch(this.deleteSnapshotEndpoint, {
                method: 'DELETE',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Delete snapshot response:', { status: response.status, result });
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', null, 5000);
                this.loadSnapshots(); // Reload to update UI
            } else {
                this.showToast(`✗ ${result.message || 'Deletion failed'}`, 'error', null, 15000);
            }
        } catch (error) {
            console.error('Delete snapshot error:', error);
            this.removeToast(toastId);
            this.showToast(`✗ Error deleting snapshot: ${error.message}`, 'error', null, 15000);
        }
    }

    async deleteSnapshotFromAllNodes(collection) {
        const snapshots = collection.snapshots;
        if (!confirm(`Delete all snapshots for ${collection.collectionName} (${snapshots.length} snapshots)?`)) {
            return;
        }

        const toastId = this.showToast(`Deleting snapshots for ${collection.collectionName} (${snapshots.length} snapshots)...`, 'info', 0);
        
        try {
            const promises = snapshots.map(snapshot => {
                const requestBody = {
                    CollectionName: collection.collectionName,
                    SnapshotName: snapshot.snapshotName,
                    Source: snapshot.source
                };

                if (snapshot.source === 'QdrantApi') {
                    if (snapshot.nodeUrl && snapshot.nodeUrl.trim() !== '' && snapshot.nodeUrl !== 'S3') {
                        requestBody.NodeUrls = [snapshot.nodeUrl];
                    }
                } else if (snapshot.source === 'KubernetesStorage') {
                    if (
                        snapshot.podName && snapshot.podName.trim() !== '' && snapshot.podName !== 'S3' && snapshot.podName !== 'unknown' &&
                        snapshot.podNamespace && snapshot.podNamespace.trim() !== '' && snapshot.podNamespace !== 'S3'
                    ) {
                        requestBody.Pods = [{
                            PodName: snapshot.podName,
                            PodNamespace: snapshot.podNamespace
                        }];
                    }
                }

                return fetch(this.deleteSnapshotEndpoint, {
                    method: 'DELETE',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify(requestBody)
                });
            });

            const results = await Promise.all(promises);
            this.removeToast(toastId);

            const successCount = results.filter(r => r.ok).length;
            if (successCount === results.length) {
                this.showToast(`✓ Successfully deleted snapshots for ${collection.collectionName} (${snapshots.length} snapshots)`, 'success', null, 5000);
                this.loadSnapshots(); // Reload to update UI
            } else {
                this.showToast(`⚠ Deleted ${successCount}/${snapshots.length} snapshots`, 'warning', null, 5000);
                this.loadSnapshots(); // Reload to update UI
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error deleting snapshots: ${error.message}`, 'error', null, 15000);
        }
    }

    showRecoverFromUrlDialog(nodeUrl) {
        // Create modal overlay
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        
        // Create modal dialog
        const modal = document.createElement('div');
        modal.className = 'modal-dialog';
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-cloud-download-alt"></i> Recover Collection from URL</h3>
                <button class="modal-close">&times;</button>
            </div>
            <div class="modal-body">
                <div class="form-group">
                    <label for="recoverFromUrlNodeUrl">Node URL:</label>
                    <input type="text" id="recoverFromUrlNodeUrl" value="${nodeUrl}" readonly class="form-input" />
                </div>
                <div class="form-group">
                    <label for="recoverFromUrlCollectionName">Collection Name:</label>
                    <input type="text" id="recoverFromUrlCollectionName" placeholder="Enter collection name" class="form-input" required />
                </div>
                <div class="form-group">
                    <label for="recoverFromUrlSnapshotUrl">Snapshot URL:</label>
                    <input type="text" id="recoverFromUrlSnapshotUrl" placeholder="Enter snapshot URL (e.g., s3://...)" class="form-input" required />
                </div>
                <div class="form-group">
                    <label for="recoverFromUrlChecksum">Checksum (optional):</label>
                    <input type="text" id="recoverFromUrlChecksum" placeholder="Enter snapshot checksum" class="form-input" />
                </div>
                <div class="form-group">
                    <label for="recoverFromUrlSnapshotPriority">Snapshot Priority:</label>
                    <select id="recoverFromUrlSnapshotPriority" class="form-select">
                        <option value="Snapshot" selected>Snapshot (prefer snapshot data)</option>
                        <option value="Replica">Replica (prefer existing data)</option>
                        <option value="NoSync">NoSync (restore without sync)</option>
                    </select>
                    <small class="form-hint">Source of truth for snapshot recovery</small>
                </div>
                <div class="form-group">
                    <label class="checkbox-label">
                        <input type="checkbox" id="recoverFromUrlWaitForResult" />
                        Wait for result
                    </label>
                </div>
            </div>
            <div class="modal-footer">
                <button class="btn-secondary modal-cancel">Cancel</button>
                <button class="btn-primary modal-submit"><i class="fas fa-cloud-download-alt"></i> Recover</button>
            </div>
        `;
        
        overlay.appendChild(modal);
        document.body.appendChild(overlay);
        
        // Focus on collection name input
        setTimeout(() => {
            const collectionNameInput = overlay.querySelector('#recoverFromUrlCollectionName');
            if (collectionNameInput) {
                collectionNameInput.focus();
            }
        }, 100);
        
        // Close handlers
        const closeModal = () => {
            overlay.classList.add('closing');
            setTimeout(() => overlay.remove(), 300);
        };
        
        overlay.querySelector('.modal-close').addEventListener('click', closeModal);
        overlay.querySelector('.modal-cancel').addEventListener('click', closeModal);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeModal();
        });
        
        // Submit handler
        let isSubmitting = false;
        const submitButton = overlay.querySelector('.modal-submit');
        
        submitButton.addEventListener('click', async () => {
            if (isSubmitting) {
                console.log('Already submitting, ignoring click');
                return;
            }
            
            isSubmitting = true;
            submitButton.disabled = true;
            
            console.log('Submit button clicked');
            
            // Use overlay.querySelector to avoid conflicts with other modals
            const collectionNameInput = overlay.querySelector('#recoverFromUrlCollectionName');
            const snapshotUrlInput = overlay.querySelector('#recoverFromUrlSnapshotUrl');
            const snapshotChecksumInput = overlay.querySelector('#recoverFromUrlChecksum');
            const snapshotPriorityInput = overlay.querySelector('#recoverFromUrlSnapshotPriority');
            const waitForResultInput = overlay.querySelector('#recoverFromUrlWaitForResult');
            
            console.log('Form elements:', {
                collectionNameInput,
                snapshotUrlInput,
                snapshotChecksumInput,
                snapshotPriorityInput,
                waitForResultInput
            });
            

            if (!collectionNameInput || !snapshotUrlInput) {
                console.error('Form elements not found in DOM!');
                this.showToast('Form error - please try again', 'error', null, 15000);
                isSubmitting = false;
                submitButton.disabled = false;
                return;
            }
            
            const collectionName = collectionNameInput.value.trim();
            const snapshotUrl = snapshotUrlInput.value.trim();
            const snapshotChecksum = snapshotChecksumInput?.value.trim() || null;
            const snapshotPriority = snapshotPriorityInput?.value || 'Snapshot';
            const waitForResult = waitForResultInput?.checked ?? false;
            
            console.log('Recover from URL form values:', {
                collectionName,
                collectionNameLength: collectionName.length,
                snapshotUrl,
                snapshotUrlLength: snapshotUrl.length,
                snapshotChecksum,
                snapshotPriority,
                waitForResult,
                nodeUrl
            });
            
            if (!collectionName || !snapshotUrl) {
                console.log('Validation failed - missing required fields');
                
                const missingFields = [];
                if (!collectionName) missingFields.push('Collection Name');
                if (!snapshotUrl) missingFields.push('Snapshot URL');
                
                this.showToast(
                    `Please fill in: ${missingFields.join(', ')}`, 
                    'error', 
                    'Missing Required Fields',
                    15000
                );
                isSubmitting = false;
                submitButton.disabled = false;
                
                // Focus on first empty field
                if (!collectionName) {
                    collectionNameInput.focus();
                    collectionNameInput.classList.add('input-error');
                    setTimeout(() => collectionNameInput.classList.remove('input-error'), 2000);
                } else if (!snapshotUrl) {
                    snapshotUrlInput.focus();
                    snapshotUrlInput.classList.add('input-error');
                    setTimeout(() => snapshotUrlInput.classList.remove('input-error'), 2000);
                }
                return;
            }
            
            console.log('Validation passed, closing modal and calling recoverCollectionFromUrl');
            closeModal();
            await this.recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, snapshotPriority, waitForResult);
        });
        
        // Enter key handler for form inputs
        ['#recoverFromUrlCollectionName', '#recoverFromUrlSnapshotUrl', '#recoverFromUrlChecksum'].forEach(selector => {
            const input = overlay.querySelector(selector);
            if (input) {
                input.addEventListener('keypress', (e) => {
                    if (e.key === 'Enter') {
                        submitButton.click();
                    }
                });
            }
        });
    }

    async recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, snapshotPriority, waitForResult) {
        const toastId = this.showToast(
            `Recovering collection '${collectionName}' from URL on node ${nodeUrl}...`, 
            'info',
            null,
            0
        );
        
        try {
            const requestBody = {
                TargetNodeUrl: nodeUrl,
                CollectionName: collectionName,
                SnapshotUrl: snapshotUrl,
                WaitForResult: waitForResult
            };

            // Add SnapshotChecksum only if it has a value
            if (snapshotChecksum && snapshotChecksum.trim() !== '') {
                requestBody.SnapshotChecksum = snapshotChecksum;
            }

            // Add SnapshotPriority if provided
            if (snapshotPriority) {
                requestBody.SnapshotPriority = snapshotPriority;
            }

            console.log('Recover from URL request:', requestBody);

            const response = await fetch(this.recoverFromSnapshotEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Recover from URL response:', { status: response.status, result });
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', null, 5000);
                // Reload data to reflect changes
                setTimeout(() => this.refresh(), 2000);
            } else {
                this.showToast(`✗ ${result.message || 'Recovery failed'}`, 'error', null, 15000);
            }
        } catch (error) {
            console.error('Recover from URL error:', error);
            this.removeToast(toastId);
            this.showToast(`✗ Error recovering collection: ${error.message}`, 'error', 15000);
        }
    }

    updateUI(clusterState) {
        this.updateOverallStatus(clusterState);
        // Store cluster issues (already includes issues from all nodes, aggregated by backend)
        this.clusterIssues = clusterState.health.issues || [];
        
        // Store warnings (already includes warnings from all nodes, aggregated by backend)
        this.clusterWarnings = clusterState.health.warnings || [];
        
        // Store StatefulSet name from API response
        this.statefulSetName = clusterState.statefulSetName;
        
        // Update combined issues and warnings display
        this.updateCombinedIssues();
        this.updateWarnings();
        this.updateNodes(clusterState.nodes);
    }

    updateOverallStatus(clusterState) {
        const statusBadge = document.getElementById('statusBadge');
        const statusText = document.getElementById('statusText');
        const healthyNodes = document.getElementById('healthyNodes');
        const healthPercentage = document.getElementById('healthPercentage');
        const leaderNode = document.getElementById('leaderNode');

        // Update status badge - convert numeric status to text
        const statusTextValue = this.getStatusText(clusterState.status);
        const statusClass = this.getStatusClass(clusterState.status);
        
        statusText.textContent = statusTextValue;
        statusBadge.className = `status-badge ${statusClass}`;

        // Update metrics
        healthyNodes.textContent = `${clusterState.health.healthyNodes}/${clusterState.health.totalNodes}`;
        healthPercentage.textContent = `${clusterState.health.healthPercentage.toFixed(1)}%`;
        leaderNode.textContent = clusterState.health.leader || 'None';
    }

    updateCombinedIssues() {
        const issuesCard = document.getElementById('issuesCard');
        const issuesList = document.getElementById('issuesList');

        const totalIssues = this.clusterIssues.length + this.collectionIssues.length;

        if (totalIssues === 0) {
            issuesCard.style.display = 'none';
            return;
        }

        issuesCard.style.display = 'block';
        issuesList.innerHTML = '';

        // Add cluster issues section (includes node issues aggregated by backend)
        if (this.clusterIssues.length > 0) {
            const clusterSection = document.createElement('div');
            clusterSection.className = 'issues-section';
            clusterSection.innerHTML = `
                <div class="issues-section-header">
                    <i class="fas fa-server"></i> Cluster Issues (${this.clusterIssues.length})
                </div>
            `;
            
            const clusterList = document.createElement('ul');
            clusterList.className = 'issues-sublist';
            this.clusterIssues.forEach(issue => {
                const li = document.createElement('li');
                li.className = 'issue-item cluster-issue';
                li.textContent = issue;
                clusterList.appendChild(li);
            });
            
            clusterSection.appendChild(clusterList);
            issuesList.appendChild(clusterSection);
        }

        // Add collection issues section
        if (this.collectionIssues.length > 0) {
            const collectionSection = document.createElement('div');
            collectionSection.className = 'issues-section';
            collectionSection.innerHTML = `
                <div class="issues-section-header">
                    <i class="fas fa-database"></i> Collection Issues (${this.collectionIssues.length})
                </div>
            `;
            
            const collectionList = document.createElement('ul');
            collectionList.className = 'issues-sublist';
            this.collectionIssues.forEach(issue => {
                const li = document.createElement('li');
                li.className = 'issue-item collection-issue';
                li.textContent = issue;
                collectionList.appendChild(li);
            });
            
            collectionSection.appendChild(collectionList);
            issuesList.appendChild(collectionSection);
        }
    }


    updateWarnings() {
        const warningsCard = document.getElementById('warningsCard');
        const warningsList = document.getElementById('warningsList');

        // Warnings already include all node warnings aggregated by backend
        if (!this.clusterWarnings || this.clusterWarnings.length === 0) {
            warningsCard.style.display = 'none';
            return;
        }

        warningsCard.style.display = 'block';
        warningsList.innerHTML = '';

        this.clusterWarnings.forEach(warning => {
            const li = document.createElement('li');
            li.className = 'warning-item';
            li.textContent = warning;
            warningsList.appendChild(li);
        });
    }

    updateNodes(nodes) {
        console.log('Updating nodes UI with:', nodes);
        
        // Save currently open node menus before clearing DOM
        this.openNodeMenus.clear();
        document.querySelectorAll('.node-actions-menu-button-header.active').forEach(btn => {
            const card = btn.closest('.node-card');
            if (card) {
                const peerId = card.querySelector('.node-id')?.textContent?.split('\n')[0]?.trim();
                if (peerId) {
                    this.openNodeMenus.add(peerId);
                    console.log('Saved open menu state for node:', peerId);
                }
            }
        });
        
        const nodesGrid = document.getElementById('nodesGrid');
        nodesGrid.innerHTML = '';

        // Store nodes for StatefulSet management (do this BEFORE early return)
        this.clusterNodes = nodes || [];

        if (!nodes || nodes.length === 0) {
            console.log('No nodes available to display');
            nodesGrid.innerHTML = '<p>No nodes available</p>';
            return;
        }


        console.log(`Creating cards for ${nodes.length} nodes`);
        nodes.forEach(node => {
            console.log('Node data:', {
                peerId: node.peerId,
                podName: node.podName,
                namespace: node.namespace,
                statefulSetName: node.statefulSetName
            });
            const nodeCard = this.createNodeCard(node);
            nodesGrid.appendChild(nodeCard);
        });

        console.log('Nodes UI updated');
        // Restore sticky actions panel open state after DOM update (so it stays open on auto-refresh)
        this.restoreStickyActionsMenuState();
        // Note: loadCollectionSizes is called separately in refresh() to avoid duplicate calls
    }


    createNodeCard(node) {
        const card = document.createElement('div');
        card.className = `node-card ${node.isHealthy ? 'healthy' : 'unhealthy'}`;

        const header = document.createElement('div');
        header.className = 'node-header';

        const nodeId = document.createElement('div');
        nodeId.className = 'node-id';
        nodeId.textContent = node.peerId;
        nodeId.title = node.peerId; // Show full peer ID on hover

        if (node.isLeader) {
            const leaderBadge = document.createElement('span');
            leaderBadge.className = 'leader-badge';
            leaderBadge.textContent = 'LEADER';
            nodeId.appendChild(leaderBadge);
        }

        header.appendChild(nodeId);

        // Create actions menu button in header (three dots in top-right corner)
        const actionsMenuButton = document.createElement('button');
        actionsMenuButton.className = 'node-actions-menu-button-header';
        actionsMenuButton.innerHTML = '<i class="fas fa-ellipsis-v"></i>';
        actionsMenuButton.setAttribute('aria-label', 'Node actions');
        
        const actionsMenuContainer = document.createElement('div');
        actionsMenuContainer.className = 'node-actions-menu-container-header';
        actionsMenuContainer.appendChild(actionsMenuButton);
        
        header.appendChild(actionsMenuContainer);

        const details = document.createElement('div');
        details.className = 'node-details';

        // Pod name (if available)
        if (node.podName) {
            const podDetailsContainer = document.createElement('div');
            podDetailsContainer.className = 'pod-details-container';
            
            const podDetail = this.createNodeDetail('Pod', node.podName);
            podDetailsContainer.appendChild(podDetail);
            
            details.appendChild(podDetailsContainer);
        }

        // URL without dashboard button
        const urlDetail = this.createNodeDetail('URL', node.url);
        details.appendChild(urlDetail);

        // Version (if available)
        if (node.version) {
            const versionDetail = this.createNodeDetail('Version', node.version);
            details.appendChild(versionDetail);
        }

        // Namespace (if available)
        if (node.namespace) {
            const namespaceDetail = this.createNodeDetail('Namespace', node.namespace);
            details.appendChild(namespaceDetail);
        }

        const usedBytes = node?.storage?.usedBytes;
        const capacityBytes = node?.storage?.capacityBytes;
        const usagePercentRaw = node?.storage?.usagePercent;
        if (typeof usedBytes === 'number' && typeof capacityBytes === 'number' && typeof usagePercentRaw === 'number') {
            const usagePercent = Math.max(0, Math.min(100, usagePercentRaw));
            const usageBar = document.createElement('div');
            usageBar.className = 'node-disk-usage';
            usageBar.innerHTML = `
                <div class="node-disk-usage-header">
                    <span class="node-disk-usage-label">Disk usage</span>
                    <span class="node-disk-usage-value">${usagePercent.toFixed(2)}%</span>
                </div>
                <div class="node-disk-progress">
                    <div class="node-disk-progress-fill" style="width: ${usagePercent.toFixed(2)}%"></div>
                </div>
                <div class="node-disk-usage-capacity">${this.formatSize(usedBytes)} / ${this.formatSize(capacityBytes)}</div>
            `;
            details.appendChild(usageBar);
        }

        // Create dropdown menu (attached to the header button)
        const actionsDropdown = document.createElement('div');
        actionsDropdown.className = 'node-actions-dropdown';
        
        // Generate Exec action (always show, but handle missing podName)
        const execAction = document.createElement('button');
        execAction.className = 'node-action-item';
        execAction.innerHTML = '<i class="fas fa-terminal"></i> Generate exec';
        execAction.addEventListener('click', (e) => {
            e.stopPropagation();
            
            if (!node.podName) {
                alert('Cannot generate exec command: Pod information is not available.\n\nThis node is not running in a Kubernetes cluster.');
                actionsDropdown.classList.remove('show');
                actionsMenuButton.classList.remove('active');
                this.openNodeMenus.delete(node.peerId);
                return;
            }
            
            const namespace = node.namespace || 'qdrant';
            const command = `kubectl exec -n ${namespace} -c qdrant --stdin --tty ${node.podName} -- /bin/bash`;
            
            const textarea = document.createElement('textarea');
            textarea.value = command;
            textarea.setAttribute('readonly', '');
            textarea.style.position = 'absolute';
            textarea.style.left = '-9999px';
            document.body.appendChild(textarea);
            
            try {
                textarea.select();
                document.execCommand('copy');
                document.body.removeChild(textarea);
                
                execAction.innerHTML = '<i class="fas fa-check"></i> Copied!';
                console.log('Command copied:', command);
                
                setTimeout(() => {
                    execAction.innerHTML = '<i class="fas fa-terminal"></i> Generate exec';
                }, 2000);
            } catch (err) {
                console.error('Failed to copy command:', err);
                document.body.removeChild(textarea);
                
                execAction.innerHTML = '<i class="fas fa-times"></i> Failed to copy';
                
                setTimeout(() => {
                    execAction.innerHTML = '<i class="fas fa-terminal"></i> Generate exec';
                }, 2000);
            }
            
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(execAction);
        
        // Open Dashboard action
        const dashboardAction = document.createElement('button');
        dashboardAction.className = 'node-action-item';
        dashboardAction.innerHTML = '<i class="fas fa-chart-line"></i> Open Dashboard';
        dashboardAction.addEventListener('click', (e) => {
            e.stopPropagation();
            const browserNodeUrl = node.browserUrl || node.url;
            const dashboardUrl = new URL(browserNodeUrl);
            dashboardUrl.pathname = '/dashboard';
            window.open(dashboardUrl.toString(), '_blank');
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(dashboardAction);
        
        // View Logs action
        const viewLogsAction = document.createElement('button');
        viewLogsAction.className = 'node-action-item';
        viewLogsAction.innerHTML = '<i class="fas fa-file-alt"></i> View Logs';
        viewLogsAction.addEventListener('click', (e) => {
            e.stopPropagation();
            this.openQdrantLogs(node.podName, node.namespace, node.url);
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(viewLogsAction);
        
        // Recover from URL action
        const recoverFromUrlAction = document.createElement('button');
        recoverFromUrlAction.className = 'node-action-item';
        recoverFromUrlAction.innerHTML = '<i class="fas fa-cloud-download-alt"></i> Recover from URL';
        recoverFromUrlAction.addEventListener('click', (e) => {
            e.stopPropagation();
            this.showRecoverFromUrlDialog(node.url);
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(recoverFromUrlAction);

        // Remove Peer action (before Delete Pod)
        const removePeerAction = document.createElement('button');
        removePeerAction.className = 'node-action-item node-action-item-danger';
        removePeerAction.innerHTML = '<i class="fas fa-user-minus"></i> Remove Peer';
        removePeerAction.addEventListener('click', (e) => {
            e.stopPropagation();
            this.showRemovePeerModal(node);
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(removePeerAction);
        
        // Delete Pod action
        const deletePodAction = document.createElement('button');
        deletePodAction.className = 'node-action-item node-action-item-danger';
        deletePodAction.innerHTML = '<i class="fas fa-trash-alt"></i> Delete Pod';
        deletePodAction.addEventListener('click', (e) => {
            e.stopPropagation();
            if (!node.podName) {
                alert('Cannot delete pod: Not running in Kubernetes cluster.\n\nPod information is not available.');
                actionsDropdown.classList.remove('show');
                actionsMenuButton.classList.remove('active');
                this.openNodeMenus.delete(node.peerId);
                return;
            }
            this.deletePod(node.podName, node.namespace);
            actionsDropdown.classList.remove('show');
            actionsMenuButton.classList.remove('active');
            this.openNodeMenus.delete(node.peerId);
        });
        actionsDropdown.appendChild(deletePodAction);
        
        // Add dropdown to the menu container that was already created in header
        actionsMenuContainer.appendChild(actionsDropdown);
        
        // Add click handler to the menu button
        actionsMenuButton.addEventListener('click', (e) => {
            e.stopPropagation();
            const wasOpen = actionsMenuButton.classList.contains('active');
            // Close all other open menus
            document.querySelectorAll('.node-actions-menu-button-header.active').forEach(btn => {
                btn.classList.remove('active');
                const container = btn.parentElement;
                const menu = container?.querySelector('.node-actions-dropdown');
                if (menu) {
                    menu.classList.remove('show');
                }
                // Update state for closed menus
                const card = btn.closest('.node-card');
                if (card) {
                    const peerId = card.querySelector('.node-id')?.textContent?.split('\n')[0]?.trim();
                    if (peerId) {
                        this.openNodeMenus.delete(peerId);
                    }
                }
            });
            
            if (!wasOpen) {
                actionsMenuButton.classList.add('active');
                actionsDropdown.classList.add('show');
                // Update state for opened menu
                this.openNodeMenus.add(node.peerId);
            }
        });
        
        // Close dropdown when clicking outside
        document.addEventListener('click', (e) => {
            if (!actionsMenuContainer.contains(e.target)) {
                actionsDropdown.classList.remove('show');
                actionsMenuButton.classList.remove('active');
                // Update state when closed by outside click
                this.openNodeMenus.delete(node.peerId);
            }
        });
        
        // Restore menu state if it was open before refresh
        if (this.openNodeMenus.has(node.peerId)) {
            actionsMenuButton.classList.add('active');
            actionsDropdown.classList.add('show');
            console.log('Restored open menu state for node:', node.peerId);
        }

        card.appendChild(header);
        card.appendChild(details);

        // Short error message (if any) - show on node card
        if (node.shortError) {
            const errorDiv = document.createElement('div');
            errorDiv.className = 'node-error';
            errorDiv.textContent = `Error: ${node.shortError}`;
            card.appendChild(errorDiv);
        }

        // Collections section (will be populated later via updateCollectionSizes)
        const collectionsSection = document.createElement('div');
        collectionsSection.className = 'collections-section';
        card.appendChild(collectionsSection);

        return card;
    }

    createNodeDetail(label, value) {
        const detail = document.createElement('div');
        detail.className = 'node-detail';

        const labelSpan = document.createElement('span');
        labelSpan.className = 'node-detail-label';
        labelSpan.textContent = label + ':';

        const valueSpan = document.createElement('span');
        valueSpan.className = 'node-detail-value';
        valueSpan.textContent = value;

        detail.appendChild(labelSpan);
        detail.appendChild(valueSpan);

        return detail;
    }

    showRefreshAnimation() {
        const statusCard = document.getElementById('overallStatus');
        const refreshButton = document.getElementById('manualRefresh');
        const stickyRefreshButton = document.getElementById('stickyManualRefresh');
        const refreshIndicator = document.getElementById('refreshIndicator');
        
        // Remove previous animation if it exists
        this.hideRefreshAnimation();
        
        statusCard.classList.add('refreshing');
        refreshButton.classList.add('refreshing');
        if (stickyRefreshButton) {
            stickyRefreshButton.classList.add('refreshing');
        }
        refreshIndicator.classList.add('refreshing');

        // Remove animation classes after animation completes
        setTimeout(() => {
            statusCard.classList.remove('refreshing');
        }, 800);
    }

    hideRefreshAnimation() {
        const statusCard = document.getElementById('overallStatus');
        const refreshButton = document.getElementById('manualRefresh');
        const stickyRefreshButton = document.getElementById('stickyManualRefresh');
        const refreshIndicator = document.getElementById('refreshIndicator');
        
        statusCard.classList.remove('refreshing');
        refreshButton.classList.remove('refreshing');
        if (stickyRefreshButton) {
            stickyRefreshButton.classList.remove('refreshing');
        }
        refreshIndicator.classList.remove('refreshing');
    }

    async deleteCollection(collectionName, deletionType, singleNode = false, nodeUrl = null, podName = null, podNamespace = null) {
        const typeLabel = deletionType === 'Api' ? 'API' : 'Disk';
        // Show podName if available and not 'unknown', otherwise show nodeUrl
        const nodeIdentifier = podName && podName !== 'unknown' ? podName : nodeUrl;
        const scopeLabel = singleNode ? `on ${nodeIdentifier}` : 'on all nodes';
        
        if (!confirm(`Are you sure you want to delete collection '${collectionName}' via ${typeLabel} ${scopeLabel}?\n\nThis action cannot be undone!`)) {
            return;
        }

        const toastId = this.showToast(
            `Deleting collection '${collectionName}' via ${typeLabel} ${scopeLabel}...`,
            'info',
            'Deletion in progress',
            0,
            true
        );

        try {
            const requestBody = {
                CollectionName: collectionName,
                DeletionType: deletionType,
                SingleNode: singleNode
            };

            if (singleNode) {
                if (deletionType === 'Api') {
                    if (nodeUrl && nodeUrl.trim() !== '') {
                        requestBody.NodeUrl = nodeUrl;
                    }
                } else {
                    if (podName && podName.trim() !== '') {
                        requestBody.PodName = podName;
                    }
                    if (podNamespace && podNamespace.trim() !== '') {
                        requestBody.PodNamespace = podNamespace;
                    }
                }
            }

            console.log('Delete collection request:', requestBody);

            const response = await fetch(this.deleteCollectionEndpoint, {
                method: 'DELETE',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Delete collection response:', { status: response.status, result });
            
            if (response.ok && result.success) {
                this.showDeletionResultToast(toastId, collectionName, result, true);
                // Refresh after a short delay to allow deletion to complete
                setTimeout(() => this.refresh(), 1000);
            } else {
                this.showDeletionResultToast(toastId, collectionName, result, false);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error deleting collection: ${error.message}`, 'error', 'Deletion failed', 15000);
        }
    }

    showDeletionResultToast(toastId, collectionName, result, success) {
        let detailsHtml = '';
        
        if (result.results && Object.keys(result.results).length > 0) {
            const resultsList = Object.entries(result.results)
                .map(([node, nodeResult]) => {
                    const icon = nodeResult.success ? '✓' : '✗';
                    const error = nodeResult.error ? ` - ${nodeResult.error}` : '';
                    return `${icon} ${node}${error}`;
                })
                .join('<br>');
            detailsHtml = `<div style="margin-top: 8px; font-size: 0.9em;">${resultsList}</div>`;
        }
        
        const message = `${result.message}${detailsHtml}`;
        const type = success ? 'success' : 'error';
        const title = success ? '✓ Deletion successful' : '✗ Deletion failed';
        
        this.updateToast(toastId, message, type, title);
    }

    // Helper to detect and format timeout errors
    getErrorMessage(error) {
        // Check for timeout/network errors
        if (error.name === 'AbortError') {
            return 'Request timeout - server took too long to respond';
        }
        
        // Check for common network error patterns
        if (error.message === 'Failed to fetch' || error.message.includes('NetworkError')) {
            // Check if it might be a timeout (browser doesn't always expose this)
            return 'Network error or request timeout - unable to connect to server';
        }
        
        if (error.message.includes('timeout')) {
            return 'Request timeout - server took too long to respond';
        }
        
        // For HTTP errors, provide more detail
        if (error.message.includes('HTTP error')) {
            return error.message;
        }
        
        // Generic error message
        return error.message || 'Unknown error occurred';
    }

    addClusterError(message) {
        // Create error message with retry info if auto-refresh is enabled
        let errorMessage = `Error loading cluster status: ${message}`;
        if (this.refreshInterval > 0) {
            errorMessage += ` (Retrying in ${this.refreshInterval / 1000} seconds)`;
        }
        
        // Add to cluster issues if not already present
        if (!this.clusterIssues.includes(errorMessage)) {
            this.clusterIssues.push(errorMessage);
            this.updateCombinedIssues();
        }
        
        // Auto-remove after 10 seconds if auto-refresh is enabled
        // (it will be re-added if the error persists on next refresh)
        if (this.refreshInterval > 0) {
            setTimeout(() => {
                const index = this.clusterIssues.indexOf(errorMessage);
                if (index > -1) {
                    this.clusterIssues.splice(index, 1);
                    this.updateCombinedIssues();
                }
            }, 10000);
        }
    }

    // Snapshot management methods
    async showNodeSelectionDialog(collection, action) {
        // Create modal overlay
        const modal = document.createElement('div');
        modal.className = 'modal-overlay';
        modal.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.5);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
        `;

        const modalContent = document.createElement('div');
        modalContent.className = 'node-selection-modal-content';
        modalContent.style.cssText = `
            background: white;
            padding: 24px;
            border-radius: 8px;
            max-width: 600px;
            width: 90%;
            max-height: 80vh;
            overflow-y: auto;
            overflow-x: hidden;
            box-shadow: 0 4px 6px rgba(0, 0, 0, 0.1);
            word-wrap: break-word;
        `;

        const actionTitles = {
            deleteApi: 'Delete Collection via API',
            deleteDisk: 'Delete Collection from Disk',
            createSnapshot: 'Create Snapshot'
        };

        const title = document.createElement('h3');
        title.textContent = `${actionTitles[action]} - Select Nodes`;
        title.style.cssText = 'margin-top: 0; word-wrap: break-word; overflow-wrap: break-word;';
        
        const description = document.createElement('p');
        description.className = 'node-selection-description';
        description.textContent = `Select the nodes where you want to ${action === 'createSnapshot' ? 'create snapshot' : 'delete collection'} for "${collection.name}":`;
        description.style.cssText = 'color: #666; word-wrap: break-word; overflow-wrap: break-word; max-width: 100%;';

        const nodesList = document.createElement('div');
        nodesList.className = 'node-selection-list';
        nodesList.style.cssText = 'margin: 16px 0; max-height: 400px; overflow-y: auto;';

        // Get nodes from collection (already an array)
        const nodes = collection.nodes || [];
        
        // For deleteDisk action, filter to only nodes with pod information
        let availableNodes = nodes;
        if (action === 'deleteDisk') {
            availableNodes = nodes.filter(n => n.podName && n.podName !== 'unknown' && n.podNamespace);
            if (availableNodes.length === 0) {
                alert('No nodes have pod information available. Cannot delete from disk.\n\nThis operation requires Kubernetes pod names, which are not available for these nodes.');
                return;
            }
        }
        
        // Select all by default
        const selectedNodes = new Set(availableNodes.map((_, index) => index));

        // Add "Select All" checkbox
        const selectAllContainer = document.createElement('div');
        selectAllContainer.className = 'node-selection-select-all';
        selectAllContainer.style.cssText = 'margin-bottom: 12px; padding-bottom: 12px; border-bottom: 2px solid #eee;';
        const selectAllCheckbox = document.createElement('input');
        selectAllCheckbox.type = 'checkbox';
        selectAllCheckbox.checked = true;
        selectAllCheckbox.id = 'select-all-nodes';
        const selectAllLabel = document.createElement('label');
        selectAllLabel.className = 'node-selection-label';
        selectAllLabel.htmlFor = 'select-all-nodes';
        selectAllLabel.textContent = ' Select All Nodes';
        selectAllLabel.style.cssText = 'font-weight: bold; cursor: pointer; user-select: none;';
        selectAllContainer.appendChild(selectAllCheckbox);
        selectAllContainer.appendChild(selectAllLabel);
        nodesList.appendChild(selectAllContainer);

        // Add node checkboxes
        availableNodes.forEach((node, index) => {
            const nodeContainer = document.createElement('div');
            nodeContainer.className = 'node-selection-node';
            nodeContainer.style.cssText = 'margin: 8px 0; padding: 8px; background: #f5f5f5; border-radius: 4px;';
            
            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.checked = true;
            checkbox.dataset.nodeIndex = index;
            checkbox.id = `node-${index}`;
            checkbox.className = 'node-checkbox';
            
            const label = document.createElement('label');
            label.className = 'node-selection-label';
            label.htmlFor = `node-${index}`;
            const displayName = node.podName && node.podName !== 'unknown' ? node.podName : node.nodeUrl;
            label.textContent = ` ${displayName}`;
            if (node.peerId) {
                label.textContent += ` (${node.peerId})`;
            }
            label.style.cssText = 'cursor: pointer; user-select: none;';
            
            checkbox.addEventListener('change', () => {
                if (checkbox.checked) {
                    selectedNodes.add(index);
                } else {
                    selectedNodes.delete(index);
                }
                // Update select all checkbox
                selectAllCheckbox.checked = selectedNodes.size === availableNodes.length;
                selectAllCheckbox.indeterminate = selectedNodes.size > 0 && selectedNodes.size < availableNodes.length;
            });
            
            nodeContainer.appendChild(checkbox);
            nodeContainer.appendChild(label);
            nodesList.appendChild(nodeContainer);
        });

        // Select All functionality
        selectAllCheckbox.addEventListener('change', () => {
            const checkboxes = nodesList.querySelectorAll('.node-checkbox');
            checkboxes.forEach((cb, index) => {
                cb.checked = selectAllCheckbox.checked;
                if (selectAllCheckbox.checked) {
                    selectedNodes.add(index);
                } else {
                    selectedNodes.delete(index);
                }
            });
        });

        const buttonsContainer = document.createElement('div');
        buttonsContainer.style.cssText = 'display: flex; gap: 8px; justify-content: flex-end; margin-top: 16px;';

        const cancelButton = document.createElement('button');
        cancelButton.textContent = 'Cancel';
        cancelButton.className = 'action-button';
        cancelButton.style.cssText = 'padding: 8px 16px;';
        cancelButton.onclick = () => {
            document.body.removeChild(modal);
        };

        const confirmButton = document.createElement('button');
        confirmButton.textContent = 'Confirm';
        confirmButton.className = action === 'createSnapshot' ? 'action-button action-button-primary' : 'action-button action-button-danger';
        confirmButton.style.cssText = 'padding: 8px 16px;';
        confirmButton.onclick = async () => {
            if (selectedNodes.size === 0) {
                alert('Please select at least one node');
                return;
            }

            document.body.removeChild(modal);

            const selectedNodeObjects = Array.from(selectedNodes).map(index => availableNodes[index]);
            
            if (action === 'deleteApi') {
                const nodeUrls = selectedNodeObjects.map(n => n.nodeUrl).filter(url => url);
                await this.deleteCollectionWithNodes(collection.name, 'Api', nodeUrls);
            } else if (action === 'deleteDisk') {
                const pods = selectedNodeObjects
                    .filter(n => n.podName && n.podName !== 'unknown' && n.podNamespace)
                    .map(n => ({ podName: n.podName, podNamespace: n.podNamespace }));
                
                if (pods.length === 0) {
                    alert('Selected nodes do not have pod information available. Cannot delete from disk.');
                    return;
                }
                
                await this.deleteCollectionWithNodes(collection.name, 'Disk', null, pods);
            } else if (action === 'createSnapshot') {
                const nodeUrls = selectedNodeObjects.map(n => n.nodeUrl).filter(url => url);
                await this.createSnapshotWithNodes(collection.name, nodeUrls);
            }
        };

        buttonsContainer.appendChild(cancelButton);
        buttonsContainer.appendChild(confirmButton);

        modalContent.appendChild(title);
        modalContent.appendChild(description);
        modalContent.appendChild(nodesList);
        modalContent.appendChild(buttonsContainer);
        modal.appendChild(modalContent);

        document.body.appendChild(modal);

        // Close on overlay click
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                document.body.removeChild(modal);
            }
        });
    }

    showShardSyncModal(collection, sourceNodeInfo, selectedShards) {
        // Create modal overlay
        const modal = document.createElement('div');
        modal.className = 'modal-overlay';
        modal.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.6);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
            backdrop-filter: blur(2px);
            animation: fadeIn 0.2s ease-out;
        `;

        const modalContent = document.createElement('div');
        modalContent.className = 'sync-shards-modal-content';
        modalContent.style.cssText = `
            background: white;
            padding: 0;
            border-radius: 12px;
            max-width: 550px;
            width: 90%;
            max-height: 85vh;
            overflow: hidden;
            box-shadow: 0 10px 40px rgba(0, 0, 0, 0.2);
            animation: slideIn 0.3s ease-out;
        `;

        // Header
        const header = document.createElement('div');
        header.style.cssText = `
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            padding: 14px 24px;
            border-radius: 12px 12px 0 0;
        `;
        
        const title = document.createElement('h3');
        title.textContent = '🔄 Sync Shards';
        title.style.cssText = 'margin: 0; font-size: 18px; font-weight: 600;';
        header.appendChild(title);
        
        // Content area
        const contentArea = document.createElement('div');
        contentArea.className = 'sync-shards-content-area';
        contentArea.style.cssText = 'padding: 16px 24px; overflow-y: auto; max-height: calc(85vh - 160px);';
        
        const sourceDisplay = sourceNodeInfo.podName && sourceNodeInfo.podName !== 'unknown' 
            ? sourceNodeInfo.podName 
            : sourceNodeInfo.peerId;
        
        // Info card
        const infoCard = document.createElement('div');
        infoCard.className = 'sync-shards-info-card';
        infoCard.style.cssText = `
            background: #f8f9fa;
            border-left: 4px solid #667eea;
            padding: 10px;
            border-radius: 6px;
            margin-bottom: 14px;
        `;
        
        infoCard.innerHTML = `
            <div style="display: flex; flex-direction: column; gap: 5px;">
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span class="sync-shards-info-label" style="font-weight: 600; color: #495057; min-width: 95px; flex-shrink: 0; font-size: 12px;">Collection:</span>
                    <span class="sync-shards-info-value" style="color: #212529; font-family: monospace; background: white; padding: 2px 6px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0; font-size: 12px;">${collection.name}</span>
                </div>
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span class="sync-shards-info-label" style="font-weight: 600; color: #495057; min-width: 95px; flex-shrink: 0; font-size: 12px;">Source node:</span>
                    <span class="sync-shards-info-value" style="color: #212529; font-family: monospace; background: white; padding: 2px 6px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0; font-size: 12px;">${sourceDisplay}</span>
                </div>
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span class="sync-shards-info-label" style="font-weight: 600; color: #495057; min-width: 95px; flex-shrink: 0; font-size: 12px;">Shards:</span>
                    <span class="sync-shards-info-value" style="color: #212529; font-family: monospace; background: white; padding: 2px 6px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0; font-size: 12px;">${selectedShards.join(', ')}</span>
                </div>
            </div>
        `;
        
        contentArea.appendChild(infoCard);

        // Target node selection
        const targetNodeSection = document.createElement('div');
        targetNodeSection.style.cssText = 'margin-bottom: 14px;';
        
        const targetLabel = document.createElement('label');
        targetLabel.textContent = 'Target Node';
        targetLabel.style.cssText = `
            display: block;
            font-weight: 600;
            margin-bottom: 5px;
            color: #495057;
            font-size: 12px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        `;
        
        const targetSelect = document.createElement('select');
        targetSelect.style.cssText = `
            width: 100%;
            padding: 9px;
            border: 2px solid #e9ecef;
            border-radius: 8px;
            font-size: 13px;
            font-family: inherit;
            background: white;
            cursor: pointer;
            transition: all 0.2s;
        `;
        targetSelect.onmouseover = () => { targetSelect.style.borderColor = '#667eea'; };
        targetSelect.onmouseout = () => { targetSelect.style.borderColor = '#e9ecef'; };
        targetSelect.onfocus = () => { targetSelect.style.borderColor = '#667eea'; targetSelect.style.boxShadow = '0 0 0 3px rgba(102, 126, 234, 0.1)'; };
        targetSelect.onblur = () => { targetSelect.style.borderColor = '#e9ecef'; targetSelect.style.boxShadow = 'none'; };
        
        // Add empty option
        const emptyOption = document.createElement('option');
        emptyOption.value = '';
        emptyOption.textContent = '-- Select target node --';
        targetSelect.appendChild(emptyOption);
        
        // Add other nodes (except source)
        (collection.nodes || [])
            .filter(nodeInfo => nodeInfo.peerId && nodeInfo.peerId !== sourceNodeInfo.peerId)
            .forEach(nodeInfo => {
                const option = document.createElement('option');
                option.value = nodeInfo.peerId;
                const displayName = nodeInfo.podName && nodeInfo.podName !== 'unknown' 
                    ? nodeInfo.podName 
                    : nodeInfo.peerId;
                option.textContent = `${displayName} (${nodeInfo.peerId})`;
                targetSelect.appendChild(option);
            });
        
        targetNodeSection.appendChild(targetLabel);
        targetNodeSection.appendChild(targetSelect);
        contentArea.appendChild(targetNodeSection);

        // Transfer Method selection
        const transferMethodSection = document.createElement('div');
        transferMethodSection.style.cssText = 'margin-bottom: 14px;';
        
        const transferMethodLabel = document.createElement('label');
        transferMethodLabel.textContent = 'Transfer Method';
        transferMethodLabel.style.cssText = `
            display: block;
            font-weight: 600;
            margin-bottom: 5px;
            color: #495057;
            font-size: 12px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        `;
        
        const transferMethodSelect = document.createElement('select');
        transferMethodSelect.style.cssText = `
            width: 100%;
            padding: 9px;
            border: 2px solid #e9ecef;
            border-radius: 8px;
            font-size: 13px;
            font-family: inherit;
            background: white;
            cursor: pointer;
            transition: all 0.2s;
        `;
        transferMethodSelect.onmouseover = () => { transferMethodSelect.style.borderColor = '#667eea'; };
        transferMethodSelect.onmouseout = () => { transferMethodSelect.style.borderColor = '#e9ecef'; };
        transferMethodSelect.onfocus = () => { transferMethodSelect.style.borderColor = '#667eea'; transferMethodSelect.style.boxShadow = '0 0 0 3px rgba(102, 126, 234, 0.1)'; };
        transferMethodSelect.onblur = () => { transferMethodSelect.style.borderColor = '#e9ecef'; transferMethodSelect.style.boxShadow = 'none'; };
        
        // Add transfer method options
        const transferMethods = [
            { value: 'Snapshot', label: 'Snapshot (Default)', description: 'Transfer using snapshot - includes index and quantized data' },
            { value: 'StreamRecords', label: 'Stream Records', description: 'Stream records in batches' },
            { value: 'WalDelta', label: 'WAL Delta', description: 'Transfer only missed operations via WAL difference' }
        ];
        
        transferMethods.forEach(method => {
            const option = document.createElement('option');
            option.value = method.value;
            option.textContent = method.label;
            option.title = method.description;
            if (method.value === 'Snapshot') {
                option.selected = true;
            }
            transferMethodSelect.appendChild(option);
        });
        
        // Add description text that changes based on selection
        const transferMethodDescription = document.createElement('div');
        transferMethodDescription.style.cssText = 'font-size: 11px; color: #6c757d; margin-top: 3px; font-style: italic; line-height: 1.2;';
        transferMethodDescription.textContent = transferMethods[0].description;
        
        transferMethodSelect.onchange = () => {
            const selectedMethod = transferMethods.find(m => m.value === transferMethodSelect.value);
            transferMethodDescription.textContent = selectedMethod ? selectedMethod.description : '';
        };
        
        transferMethodSection.appendChild(transferMethodLabel);
        transferMethodSection.appendChild(transferMethodSelect);
        transferMethodSection.appendChild(transferMethodDescription);
        contentArea.appendChild(transferMethodSection);

        // Move checkbox
        const moveSection = document.createElement('div');
        moveSection.className = 'sync-shards-move-section';
        moveSection.style.cssText = `
            background: #fff3cd;
            border: 2px solid #ffc107;
            border-radius: 8px;
            padding: 10px;
            margin-bottom: 0;
        `;
        
        const moveLabel = document.createElement('label');
        moveLabel.style.cssText = 'display: flex; align-items: flex-start; cursor: pointer;';
        
        const moveCheckbox = document.createElement('input');
        moveCheckbox.type = 'checkbox';
        moveCheckbox.id = 'move-shards-modal';
        moveCheckbox.style.cssText = `
            margin-right: 10px;
            margin-top: 2px;
            width: 16px;
            height: 16px;
            cursor: pointer;
        `;
        
        const moveTextContainer = document.createElement('div');
        const moveTitle = document.createElement('div');
        moveTitle.className = 'sync-shards-move-title';
        moveTitle.textContent = '⚠️ Move shards';
        moveTitle.style.cssText = 'font-weight: 600; color: #856404; margin-bottom: 1px; font-size: 13px;';
        
        const moveDescription = document.createElement('div');
        moveDescription.className = 'sync-shards-move-description';
        moveDescription.textContent = 'Remove shards from source node after sync';
        moveDescription.style.cssText = 'font-size: 11px; color: #856404; line-height: 1.2;';
        
        moveTextContainer.appendChild(moveTitle);
        moveTextContainer.appendChild(moveDescription);
        
        moveLabel.appendChild(moveCheckbox);
        moveLabel.appendChild(moveTextContainer);
        moveSection.appendChild(moveLabel);
        contentArea.appendChild(moveSection);

        // Footer with buttons
        const footer = document.createElement('div');
        footer.className = 'sync-shards-footer';
        footer.style.cssText = `
            padding: 14px 24px;
            background: #f8f9fa;
            border-top: 1px solid #e9ecef;
            display: flex;
            gap: 12px;
            justify-content: flex-end;
        `;

        const cancelButton = document.createElement('button');
        cancelButton.className = 'sync-shards-cancel-button';
        cancelButton.textContent = 'Cancel';
        cancelButton.style.cssText = `
            padding: 10px 24px;
            border: 2px solid #6c757d;
            background: white;
            color: #6c757d;
            border-radius: 8px;
            font-size: 14px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s;
        `;
        cancelButton.onmouseover = () => { cancelButton.style.background = '#6c757d'; cancelButton.style.color = 'white'; };
        cancelButton.onmouseout = () => { cancelButton.style.background = 'white'; cancelButton.style.color = '#6c757d'; };
        cancelButton.onclick = () => {
            modal.style.animation = 'fadeOut 0.2s ease-out';
            setTimeout(() => document.body.removeChild(modal), 200);
        };

        const confirmButton = document.createElement('button');
        confirmButton.className = 'sync-shards-confirm-button';
        confirmButton.textContent = '🔄 Sync Shards';
        confirmButton.style.cssText = `
            padding: 10px 24px;
            border: none;
            background: linear-gradient(135deg, #667eea 0%, #764ba2 100%);
            color: white;
            border-radius: 8px;
            font-size: 14px;
            font-weight: 600;
            cursor: pointer;
            transition: all 0.2s;
            box-shadow: 0 4px 12px rgba(102, 126, 234, 0.3);
        `;
        confirmButton.onmouseover = () => { confirmButton.style.transform = 'translateY(-2px)'; confirmButton.style.boxShadow = '0 6px 16px rgba(102, 126, 234, 0.4)'; };
        confirmButton.onmouseout = () => { confirmButton.style.transform = 'translateY(0)'; confirmButton.style.boxShadow = '0 4px 12px rgba(102, 126, 234, 0.3)'; };
        confirmButton.onclick = async () => {
            const targetPeerId = targetSelect.value;
            const isMoveShards = moveCheckbox.checked;
            const shardTransferMethod = transferMethodSelect.value;
            
            if (!targetPeerId) {
                alert('Please select a target node');
                targetSelect.focus();
                return;
            }

            const operationType = isMoveShards ? 'move' : 'sync';
            const targetDisplayName = targetSelect.options[targetSelect.selectedIndex].textContent;
            
            if (!confirm(`Are you sure you want to ${operationType} shards [${selectedShards.join(', ')}] to ${targetDisplayName} using ${shardTransferMethod} method?`)) {
                return;
            }

            modal.style.animation = 'fadeOut 0.2s ease-out';
            setTimeout(() => document.body.removeChild(modal), 200);

            try {
                const requestBody = {
                    sourcePeerId: sourceNodeInfo.peerId,
                    targetPeerId: targetPeerId,
                    collectionName: collection.name,
                    shardIdsToReplicate: selectedShards,
                    isMoveShards: isMoveShards,
                    shardTransferMethod: shardTransferMethod
                };
                
                const response = await fetch(this.replicateShardsEndpoint, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify(requestBody)
                });

                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error.details || `Failed to ${operationType} shards`);
                }

                this.showToast(`Shard ${operationType} initiated successfully using ${shardTransferMethod} method`, 'success', 'Sync Started', 5000);
                setTimeout(() => this.refresh(), 2000);
            } catch (error) {
                this.showToast(`Error: ${error.message}`, 'error', 'Sync Failed', 10000);
            }
        };

        footer.appendChild(cancelButton);
        footer.appendChild(confirmButton);

        modalContent.appendChild(header);
        modalContent.appendChild(contentArea);
        modalContent.appendChild(footer);
        modal.appendChild(modalContent);

        // Add CSS animations
        const style = document.createElement('style');
        style.textContent = `
            @keyframes fadeIn {
                from { opacity: 0; }
                to { opacity: 1; }
            }
            @keyframes fadeOut {
                from { opacity: 1; }
                to { opacity: 0; }
            }
            @keyframes slideIn {
                from { transform: translateY(-20px); opacity: 0; }
                to { transform: translateY(0); opacity: 1; }
            }
        `;
        document.head.appendChild(style);

        document.body.appendChild(modal);

        // Close on overlay click
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                modal.style.animation = 'fadeOut 0.2s ease-out';
                setTimeout(() => document.body.removeChild(modal), 200);
            }
        });
        
        // Focus on select
        setTimeout(() => targetSelect.focus(), 100);
    }

    showReshardingModal(collection) {
        // Create modal overlay
        const modal = document.createElement('div');
        modal.className = 'modal-overlay';
        modal.style.cssText = `
            position: fixed;
            top: 0;
            left: 0;
            right: 0;
            bottom: 0;
            background: rgba(0, 0, 0, 0.6);
            display: flex;
            align-items: center;
            justify-content: center;
            z-index: 10000;
            backdrop-filter: blur(2px);
            animation: fadeIn 0.2s ease-out;
        `;

        const modalContent = document.createElement('div');
        modalContent.style.cssText = `
            background: white;
            padding: 0;
            border-radius: 12px;
            max-width: 450px;
            width: 90%;
            overflow: hidden;
            box-shadow: 0 10px 40px rgba(0, 0, 0, 0.2);
            animation: slideIn 0.3s ease-out;
        `;

        // Header
        const header = document.createElement('div');
        header.style.cssText = `
            background: linear-gradient(135deg, #9c27b0 0%, #7b1fa2 100%);
            color: white;
            padding: 14px 24px;
            border-radius: 12px 12px 0 0;
        `;
        
        const title = document.createElement('h3');
        title.textContent = '🔧 Start Resharding';
        title.style.cssText = 'margin: 0; font-size: 18px; font-weight: 600;';
        header.appendChild(title);
        
        // Content area
        const contentArea = document.createElement('div');
        contentArea.style.cssText = 'padding: 16px 24px;';
        
        // Info card
        const infoCard = document.createElement('div');
        infoCard.style.cssText = `
            background: #f8f9fa;
            border-left: 4px solid #9c27b0;
            padding: 10px;
            border-radius: 6px;
            margin-bottom: 14px;
        `;
        
        infoCard.innerHTML = `
            <div style="display: flex; align-items: flex-start; gap: 8px;">
                <span style="font-weight: 600; color: #495057; min-width: 80px; flex-shrink: 0; font-size: 12px;">Collection:</span>
                <span style="color: #212529; font-family: monospace; background: white; padding: 2px 6px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0; font-size: 12px;">${collection.name}</span>
            </div>
        `;
        
        contentArea.appendChild(infoCard);

        // Direction selection
        const directionSection = document.createElement('div');
        directionSection.style.cssText = 'margin-bottom: 14px;';
        
        const directionLabel = document.createElement('label');
        directionLabel.textContent = 'Resharding Direction';
        directionLabel.style.cssText = `
            display: block;
            font-weight: 600;
            margin-bottom: 5px;
            color: #495057;
            font-size: 12px;
            text-transform: uppercase;
        `;
        directionSection.appendChild(directionLabel);
        
        const directionSelect = document.createElement('select');
        directionSelect.style.cssText = `
            width: 100%;
            padding: 8px;
            border: 1px solid #ced4da;
            border-radius: 4px;
            font-size: 13px;
            background: white;
        `;
        
        const upOption = document.createElement('option');
        upOption.value = 'Up';
        upOption.textContent = '⬆️ Scale Up (Add Shard)';
        directionSelect.appendChild(upOption);
        
        const downOption = document.createElement('option');
        downOption.value = 'Down';
        downOption.textContent = '⬇️ Scale Down (Remove Shard)';
        directionSelect.appendChild(downOption);
        
        directionSection.appendChild(directionSelect);
        contentArea.appendChild(directionSection);

        // Info text
        const infoText = document.createElement('div');
        infoText.style.cssText = `
            padding: 10px;
            background: #e3f2fd;
            border-radius: 6px;
            font-size: 12px;
            color: #1976d2;
            margin-bottom: 14px;
        `;
        infoText.innerHTML = `
            <strong>ℹ️ Info:</strong> Resharding will automatically redistribute data across shards. 
            <strong>Up</strong> increases the number of shards, <strong>Down</strong> decreases it.
        `;
        contentArea.appendChild(infoText);

        modalContent.appendChild(header);
        modalContent.appendChild(contentArea);

        // Footer with buttons
        const footer = document.createElement('div');
        footer.style.cssText = `
            padding: 12px 24px;
            background: #f8f9fa;
            border-top: 1px solid #dee2e6;
            display: flex;
            justify-content: flex-end;
            gap: 8px;
            border-radius: 0 0 12px 12px;
        `;

        const cancelButton = document.createElement('button');
        cancelButton.textContent = 'Cancel';
        cancelButton.style.cssText = `
            padding: 7px 16px;
            background: white;
            color: #6c757d;
            border: 1px solid #dee2e6;
            border-radius: 4px;
            cursor: pointer;
            font-size: 13px;
            transition: all 0.2s ease;
            font-weight: 500;
        `;
        cancelButton.onmouseover = () => { cancelButton.style.background = '#e9ecef'; };
        cancelButton.onmouseout = () => { cancelButton.style.background = 'white'; };
        cancelButton.onclick = () => {
            modal.style.animation = 'fadeOut 0.2s ease-out';
            setTimeout(() => document.body.removeChild(modal), 200);
        };

        const confirmButton = document.createElement('button');
        confirmButton.textContent = '🔧 Start Resharding';
        confirmButton.style.cssText = `
            padding: 7px 16px;
            background: linear-gradient(135deg, #9c27b0, #7b1fa2);
            color: white;
            border: none;
            border-radius: 4px;
            cursor: pointer;
            font-size: 13px;
            transition: all 0.2s ease;
            font-weight: 500;
            box-shadow: 0 2px 4px rgba(156, 39, 176, 0.2);
        `;
        confirmButton.onmouseover = () => {
            confirmButton.style.background = 'linear-gradient(135deg, #8e24aa, #6a1b9a)';
            confirmButton.style.transform = 'translateY(-1px)';
            confirmButton.style.boxShadow = '0 4px 8px rgba(156, 39, 176, 0.3)';
        };
        confirmButton.onmouseout = () => {
            confirmButton.style.background = 'linear-gradient(135deg, #9c27b0, #7b1fa2)';
            confirmButton.style.transform = 'translateY(0)';
            confirmButton.style.boxShadow = '0 2px 4px rgba(156, 39, 176, 0.2)';
        };

        confirmButton.onclick = async () => {
            const direction = directionSelect.value;
            const directionLabel = direction === 'Up' ? 'scale up' : 'scale down';

            if (!confirm(`Are you sure you want to start resharding for collection '${collection.name}' to ${directionLabel}?`)) {
                return;
            }

            modal.style.animation = 'fadeOut 0.2s ease-out';
            setTimeout(() => document.body.removeChild(modal), 200);

            try {
                const requestBody = {
                    collectionName: collection.name,
                    direction: direction,
                    peerId: null
                };
                
                const response = await fetch(this.startReshardingEndpoint, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify(requestBody)
                });

                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error.error || 'Failed to start resharding');
                }

                this.showToast(`Resharding operation started successfully for ${collection.name} (${directionLabel})`, 'success', 'Resharding Started', 5000);
                setTimeout(() => this.refresh(), 2000);
            } catch (error) {
                this.showToast(`Error: ${error.message}`, 'error', 'Resharding Failed', 10000);
            }
        };

        footer.appendChild(cancelButton);
        footer.appendChild(confirmButton);

        modalContent.appendChild(footer);
        modal.appendChild(modalContent);

        document.body.appendChild(modal);

        // Close on overlay click
        modal.addEventListener('click', (e) => {
            if (e.target === modal) {
                modal.style.animation = 'fadeOut 0.2s ease-out';
                setTimeout(() => document.body.removeChild(modal), 200);
            }
        });
        
        // Focus on select
        setTimeout(() => directionSelect.focus(), 100);
    }

    async deleteCollectionWithNodes(collectionName, deletionType, nodeUrls = null, pods = null) {
        const typeLabel = deletionType === 'Api' ? 'API' : 'Disk';
        const nodeCount = nodeUrls ? nodeUrls.length : (pods ? pods.length : 0);
        
        if (!confirm(`Are you sure you want to delete collection '${collectionName}' via ${typeLabel} on ${nodeCount} selected node(s)?\n\nThis action cannot be undone!`)) {
            return;
        }

        const toastId = this.showToast(
            `Deleting collection '${collectionName}' via ${typeLabel} on ${nodeCount} node(s)...`,
            'info',
            'Deletion in progress',
            0,
            true
        );

        try {
            const requestBody = {
                CollectionName: collectionName,
                DeletionType: deletionType
            };

            if (deletionType === 'Api' && nodeUrls) {
                requestBody.NodeUrls = nodeUrls;
            } else if (deletionType === 'Disk' && pods) {
                requestBody.Pods = pods.map(p => ({
                    PodName: p.podName,
                    PodNamespace: p.podNamespace
                }));
            }

            console.log('Delete collection request:', requestBody);

            const response = await fetch(this.deleteCollectionEndpoint, {
                method: 'DELETE',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Delete collection response:', { status: response.status, result });
            
            if (response.ok && result.success) {
                this.showDeletionResultToast(toastId, collectionName, result, true);
                // Refresh after a short delay to allow deletion to complete
                setTimeout(() => this.refresh(), 1000);
            } else {
                this.showDeletionResultToast(toastId, collectionName, result, false);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error deleting collection: ${error.message}`, 'error', 'Deletion failed', 15000);
        }
    }

    async createSnapshotWithNodes(collectionName, nodeUrls) {
        const nodeCount = nodeUrls.length;
        const toastId = this.showToast(
            `Creating snapshot for collection '${collectionName}' on ${nodeCount} node(s)...`,
            'info',
            'Creating Snapshot',
            0,
            true
        );

        try {
            const requestBody = {
                CollectionName: collectionName,
                NodeUrls: nodeUrls
            };

            console.log('Create snapshot request:', requestBody);

            const response = await fetch(this.createSnapshotEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Create snapshot response:', { status: response.status, result });

            if (result.success) {
                let detailsHtml = '';
                if (result.results && Object.keys(result.results).length > 0) {
                    const resultsList = Object.entries(result.results)
                        .map(([node, snapshotName]) => {
                            const icon = snapshotName ? '✓' : '✗';
                            return `${icon} ${node}${snapshotName ? `: ${snapshotName}` : ''}`;
                        })
                        .join('<br>');
                    detailsHtml = `<div style="margin-top: 8px; font-size: 0.9em;">${resultsList}</div>`;
                }

                this.updateToast(
                    toastId,
                    `${result.message || 'Snapshot creation completed'}${detailsHtml}`,
                    'success',
                    'Snapshot Created'
                );

                // Refresh snapshots after a short delay
                setTimeout(() => {
                    if (typeof this.loadSnapshots === 'function') {
                        this.loadSnapshots();
                    }
                }, 1500);
            } else {
                this.updateToast(toastId, result.message || 'Failed to create snapshot', 'error', 'Snapshot Creation Failed');
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error creating snapshot: ${error.message}`, 'error', 'Snapshot Creation Failed', 15000);
        }
    }

    async createSnapshot(collectionName, nodeUrl, onAllNodes = false, podName = null) {
        // Show podName if available and not 'unknown', otherwise show 'node'
        const nodeIdentifier = podName && podName !== 'unknown' ? podName : (onAllNodes ? null : 'node');
        const target = onAllNodes ? 'all nodes' : nodeIdentifier;
        const toastId = this.showToast(
            `Creating snapshot for collection '${collectionName}' on ${target}...`,
            'info',
            'Creating Snapshot',
            0,
            true
        );

        try {
            const requestBody = {
                CollectionName: collectionName,
                SingleNode: !onAllNodes
            };

            // Add NodeUrl only for single node creation and if it has a valid value
            if (!onAllNodes && nodeUrl && nodeUrl.trim() !== '') {
                requestBody.NodeUrl = nodeUrl;
            }

            console.log('Create snapshot request:', requestBody);

            const response = await fetch(this.createSnapshotEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            console.log('Create snapshot response:', { status: response.status, result });

            if (result.success) {
                this.updateToast(
                    toastId,
                    result.message || `Snapshot creation accepted. It will appear in the list shortly.`,
                    'success',
                    'Snapshot Creation Accepted'
                );
                
                // Refresh after a short delay to allow snapshot to be created
                setTimeout(() => this.refresh(), 2000);
            } else {
                this.updateToast(
                    toastId,
                    result.message || 'Unknown error occurred',
                    'error',
                    'Failed to Create Snapshot'
                );
            }
        } catch (error) {
            this.updateToast(
                toastId,
                error.message,
                'error',
                'Error Creating Snapshot'
            );
        }
    }

    async startRestoreReplicationFactor(collectionName) {
        const toastId = this.showToast(
            `Starting restore replication factor for '${collectionName}'...`,
            'info',
            'Restore Replication Factor',
            0,
            true
        );
        try {
            const response = await fetch(this.restoreReplicationFactorEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ collectionName })
            });
            const result = await response.json();
            if (response.ok && (response.status === 202 || response.status === 200)) {
                this.updateToast(toastId, result.message || 'Restore replication factor started.', 'success', 'Restore Replication Factor');
                this.loadJobs();
            } else if (response.status === 409) {
                this.updateToast(toastId, result.message || 'Already in progress.', 'warning', 'Restore Replication Factor');
                this.loadJobs();
            } else {
                this.updateToast(toastId, result.message || `HTTP ${response.status}`, 'error', 'Restore Replication Factor');
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error: ${this.getErrorMessage(error)}`, 'error', 'Restore Replication Factor', 10000);
        }
    }

    async cancelJobByKey(jobKey) {
        if (this.pendingJobCancellations.has(jobKey)) return;
        this.pendingJobCancellations.add(jobKey);
        this.updateJobs();

        const toastId = this.showToast(
            `Cancelling job '${jobKey}'...`,
            'info',
            'Cancel Job',
            0,
            true
        );
        try {
            const response = await fetch(this.jobsCancelEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({ key: jobKey })
            });
            const data = await response.json();

            if (response.ok) {
                this.updateToast(toastId, data.message || 'Cancellation requested.', 'success', 'Cancel Job');
                this.loadJobs();
                return;
            }

            if (response.status === 404) {
                this.updateToast(toastId, data.error || 'Job not found (already completed?)', 'warning', 'Cancel Job');
                this.loadJobs();
                return;
            }

            this.updateToast(toastId, data.error || data.message || `HTTP ${response.status}`, 'error', 'Cancel Job');
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error: ${this.getErrorMessage(error)}`, 'error', 'Cancel Job', 10000);
        } finally {
            this.pendingJobCancellations.delete(jobKey);
            this.updateJobs();
        }
    }

    async downloadSnapshot(collectionName, snapshotName, nodeUrl, podName, podNamespace, source) {
        const toastId = this.showToast(
            `Preparing download of '${snapshotName}'...`,
            'info',
            'Downloading',
            0,
            true
        );

        try {
            const requestBody = {
                CollectionName: collectionName,
                SnapshotName: snapshotName,
                Source: source
            };

            // Add optional fields only if they have valid values
            if (nodeUrl && nodeUrl.trim() !== '') {
                requestBody.NodeUrl = nodeUrl;
            }
            if (podName && podName.trim() !== '') {
                requestBody.PodName = podName;
            }
            if (podNamespace && podNamespace.trim() !== '') {
                requestBody.PodNamespace = podNamespace;
            }

            console.log('Download snapshot request:', requestBody);

            const response = await fetch(this.downloadSnapshotEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            if (!response.ok) {
                const error = await response.json();
                throw new Error(error.error || 'Failed to download snapshot');
            }

            // Get total size from Content-Length header
            const contentLength = response.headers.get('Content-Length');
            const total = contentLength ? parseInt(contentLength, 10) : 0;
            
            // Update toast with initial progress
            if (total > 0) {
                this.updateToast(
                    toastId,
                    `0% (0 / ${this.formatSize(total)})`,
                    'info',
                    `Downloading '${snapshotName}'`,
                    0,
                    false
                );
            } else {
                this.updateToast(
                    toastId,
                    `Downloading...`,
                    'info',
                    `Downloading '${snapshotName}'`,
                    null,
                    false
                );
            }

            // Read the response stream with progress tracking
            const reader = response.body.getReader();
            const chunks = [];
            let receivedLength = 0;

            while (true) {
                const { done, value } = await reader.read();

                if (done) break;

                chunks.push(value);
                receivedLength += value.length;

                // Update progress
                if (total > 0) {
                    const percent = Math.round((receivedLength / total) * 100);
                    this.updateToast(
                        toastId,
                        `${percent}% (${this.formatSize(receivedLength)} / ${this.formatSize(total)})`,
                        'info',
                        `Downloading '${snapshotName}'`,
                        percent,
                        false
                    );
                } else {
                    this.updateToast(
                        toastId,
                        `${this.formatSize(receivedLength)} received...`,
                        'info',
                        `Downloading '${snapshotName}'`,
                        null,
                        false
                    );
                }
            }

            // Combine chunks into a blob
            const blob = new Blob(chunks);
            
            // Trigger download
            const url = window.URL.createObjectURL(blob);
            const a = document.createElement('a');
            a.href = url;
            a.download = snapshotName;
            document.body.appendChild(a);
            a.click();
            window.URL.revokeObjectURL(url);
            document.body.removeChild(a);

            this.updateToast(
                toastId,
                `Downloaded successfully (${this.formatSize(receivedLength)})`,
                'success',
                `'${snapshotName}'`,
                100,
                true
            );
        } catch (error) {
            this.updateToast(
                toastId,
                error.message,
                'error',
                'Download Failed'
            );
        }
    }

    async getS3DownloadUrl(collectionName, snapshotName) {
        const toastId = this.showToast(
            `Generating download URL for '${snapshotName}'...`,
            'info',
            'Getting URL',
            0,
            true
        );

        try {
            const requestBody = {
                collectionName: collectionName,
                snapshotName: snapshotName,
                expirationHours: 1
            };

            const response = await fetch('/api/v1/snapshots/get-download-url', {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();

            if (!response.ok || !result.success) {
                throw new Error(result.message || 'Failed to generate download URL');
            }

            // Copy URL to clipboard with fallback for HTTP (non-secure context)
            let copiedToClipboard = false;
            try {
                if (navigator.clipboard && navigator.clipboard.writeText) {
                    await navigator.clipboard.writeText(result.url);
                    copiedToClipboard = true;
                } else {
                    // Fallback for HTTP contexts where Clipboard API is not available
                    const textArea = document.createElement('textarea');
                    textArea.value = result.url;
                    textArea.style.position = 'fixed';
                    textArea.style.left = '-999999px';
                    textArea.style.top = '-999999px';
                    document.body.appendChild(textArea);
                    textArea.focus();
                    textArea.select();
                    try {
                        copiedToClipboard = document.execCommand('copy');
                    } catch (err) {
                        console.error('Fallback: Could not copy text', err);
                    }
                    document.body.removeChild(textArea);
                }
            } catch (err) {
                console.error('Failed to copy to clipboard', err);
            }

            // Show URL in a prompt dialog so user can also copy manually
            const urlPreview = result.url.length > 100 
                ? result.url.substring(0, 100) + '...' 
                : result.url;
            
            const message = copiedToClipboard 
                ? `URL copied to clipboard! Valid for 1 hour.\n\nURL: ${urlPreview}`
                : `URL generated! Valid for 1 hour.\n\nPlease copy manually:\n${urlPreview}`;
            
            this.updateToast(
                toastId,
                message,
                'success',
                'Download URL Generated',
                100,
                true
            );

            // Also show in an alert for easier copying on some browsers
            setTimeout(() => {
                alert(`Download URL (copied to clipboard, valid for 1 hour):\n\n${result.url}`);
            }, 100);

        } catch (error) {
            this.updateToast(
                toastId,
                error.message,
                'error',
                'Failed to Generate URL'
            );
        }
    }

    async deletePod(podName, namespace = null) {
        const namespaceText = namespace ? ` in namespace ${namespace}` : '';
        if (!confirm(`Are you sure you want to delete pod '${podName}'${namespaceText}?\n\nThis action will restart the pod.`)) {
            return;
        }

        const toastId = this.showToast(
            `Deleting pod '${podName}'...`,
            'info',
            'Pod Deletion',
            0,
            true
        );

        try {
            const response = await fetch(this.deletePodEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify({
                    podName: podName,
                    namespace: namespace
                })
            });

            const result = await response.json();
            this.removeToast(toastId);

            if (response.ok) {
                this.showToast(result.message || `Pod '${podName}' deletion initiated successfully`, 'success', 'Success', 5000);
                // Refresh after a short delay to allow pod to be deleted
                setTimeout(() => this.refresh(), 2000);
            } else {
                this.showToast(result.error || result.details || 'Failed to delete pod', 'error', 'Deletion Failed', 15000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error deleting pod: ${error.message}`, 'error', 'Error', 15000);
        }
    }

    showRemovePeerModal(node) {
        const peerIdNum = parseInt(node.peerId, 10);
        if (isNaN(peerIdNum) || node.peerId !== String(peerIdNum)) {
            this.showToast('Remove Peer is only available for nodes with a numeric Peer ID (node must have responded to cluster).', 'error', 'Cannot remove', 8000);
            return;
        }
        const nodeDisplay = node.podName ? `${node.podName} (Peer ${node.peerId})` : `Peer ${node.peerId}`;
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        const modal = document.createElement('div');
        modal.className = 'modal-dialog';
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-user-minus"></i> Remove Peer from Cluster</h3>
                <button class="modal-close" type="button" aria-label="Close">&times;</button>
            </div>
            <div class="modal-body">
                <p>You are about to remove the following node from the Qdrant cluster:</p>
                <p class="remove-peer-node-name"><strong>${this.escapeHtml(nodeDisplay)}</strong></p>
                <p>The node will no longer participate in the cluster. Ensure shards are migrated or use <strong>Force</strong> to remove even if the peer has shards/replicas.</p>
                <label class="modal-checkbox-label">
                    <input type="checkbox" id="removePeerForce" class="modal-checkbox">
                    <span><strong>Force</strong> — remove peer even if it has shards/replicas on it (may cause data unavailability)</span>
                </label>
            </div>
            <div class="modal-footer">
                <button type="button" class="btn-secondary modal-cancel">Cancel</button>
                <button type="button" class="btn-primary modal-submit modal-submit-danger"><i class="fas fa-user-minus"></i> Remove Peer</button>
            </div>
        `;
        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        const closeModal = () => {
            overlay.removeEventListener('keydown', escHandler);
            overlay.classList.add('closing');
            setTimeout(() => overlay.remove(), 300);
        };
        const escHandler = (e) => {
            if (e.key === 'Escape') {
                e.preventDefault();
                closeModal();
            }
        };
        overlay.addEventListener('keydown', escHandler);
        overlay.querySelector('.modal-close').addEventListener('click', closeModal);
        overlay.querySelector('.modal-cancel').addEventListener('click', closeModal);
        overlay.addEventListener('click', (e) => { if (e.target === overlay) closeModal(); });

        const submitButton = overlay.querySelector('.modal-submit');
        submitButton.addEventListener('click', async () => {
            const force = overlay.querySelector('#removePeerForce').checked;
            closeModal();
            const toastId = this.showToast(`Removing peer ${node.peerId} from cluster...`, 'info', 'Remove Peer', 0, true);
            try {
                const response = await fetch(this.removePeerEndpoint, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ peerId: peerIdNum, isForceDropOperation: force })
                });
                const data = await response.json().catch(() => ({}));
                this.removeToast(toastId);
                if (response.ok) {
                    this.showToast(data.message || 'Peer removed from cluster.', 'success', 'Success', 5000);
                    setTimeout(() => this.refresh(), 1500);
                } else {
                    this.showToast(data.error || data.details || 'Failed to remove peer', 'error', 'Error', 15000);
                }
            } catch (err) {
                this.removeToast(toastId);
                this.showToast(`Error: ${err.message}`, 'error', 'Error', 15000);
            }
        });
    }

    showStatefulSetDialog() {
        // Get StatefulSet name from stored value or fall back to first node
        const firstNode = this.clusterNodes && this.clusterNodes.length > 0 ? this.clusterNodes[0] : null;
        const storedStatefulSetName = this.statefulSetName || firstNode?.statefulSetName || 'qdrant1';
        const namespace = firstNode?.namespace || 'qdrant';
        const currentReplicas = this.clusterNodes?.length || 0;

        console.log('Opening StatefulSet dialog with:', {
            storedStatefulSetName,
            namespace,
            currentReplicas,
            storedValue: this.statefulSetName,
            firstNode
        });

        // Create modal overlay
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        
        // Create modal dialog
        const modal = document.createElement('div');
        modal.className = 'modal-dialog statefulset-modal';
        
        // Always show editable StatefulSet name field with default value
        const statefulSetNameInput = `
            <div class="form-group">
                <label for="statefulSetNameInput">StatefulSet Name: <span style="color: red;">*</span></label>
                <input type="text" id="statefulSetNameInput" class="form-input" value="${storedStatefulSetName}" required />
                <small style="color: #888; display: block; margin-top: 4px;">
                    Enter the name of your Qdrant StatefulSet
                </small>
            </div>
        `;
        
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-cubes"></i> Manage StatefulSet</h3>
                <button class="modal-close">&times;</button>
            </div>
            <div class="modal-body">
                ${statefulSetNameInput}
                <div class="statefulset-info">
                    <div class="info-item">
                        <span class="info-label">Namespace:</span>
                        <span class="info-value">${namespace}</span>
                    </div>
                    <div class="info-item">
                        <span class="info-label">Current Replicas:</span>
                        <span class="info-value">${currentReplicas}</span>
                    </div>
                </div>
                <div class="form-group">
                    <label>Operation Type:</label>
                    <div class="operation-type-buttons">
                        <button type="button" class="operation-type-btn active" data-operation="rollout">
                            <i class="fas fa-redo"></i> Rollout Restart
                        </button>
                        <button type="button" class="operation-type-btn" data-operation="scale">
                            <i class="fas fa-expand-arrows-alt"></i> Scale
                        </button>
                    </div>
                </div>
                <div class="form-group scale-group" style="display: none;">
                    <label for="replicaCount">New Replica Count:</label>
                    <input type="number" id="replicaCount" min="0" value="${currentReplicas}" class="form-input" />
                </div>
            </div>
            <div class="modal-footer">
                <button class="modal-button modal-button-secondary" id="cancelStatefulSetBtn">Cancel</button>
                <button class="modal-button modal-button-primary" id="executeStatefulSetBtn">Execute</button>
            </div>
        `;
        
        overlay.appendChild(modal);
        document.body.appendChild(overlay);

        let selectedOperation = 'rollout';

        // Setup operation type toggle
        const operationButtons = modal.querySelectorAll('.operation-type-btn');
        const scaleGroup = modal.querySelector('.scale-group');
        
        operationButtons.forEach(btn => {
            btn.addEventListener('click', () => {
                operationButtons.forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                selectedOperation = btn.dataset.operation;
                
                if (selectedOperation === 'scale') {
                    scaleGroup.style.display = 'block';
                } else {
                    scaleGroup.style.display = 'none';
                }
            });
        });

        // Close handlers
        const closeModal = () => {
            overlay.remove();
        };

        modal.querySelector('.modal-close').addEventListener('click', closeModal);
        modal.querySelector('#cancelStatefulSetBtn').addEventListener('click', closeModal);
        overlay.addEventListener('click', (e) => {
            if (e.target === overlay) closeModal();
        });

        // Execute handler
        modal.querySelector('#executeStatefulSetBtn').addEventListener('click', async () => {
            // Always get StatefulSet name from input field
            const input = modal.querySelector('#statefulSetNameInput');
            const statefulSetName = input?.value.trim();
            
            if (!statefulSetName) {
                alert('Please enter the StatefulSet name');
                return;
            }
            
            const replicas = selectedOperation === 'scale' 
                ? parseInt(modal.querySelector('#replicaCount').value)
                : null;

            if (selectedOperation === 'scale' && (replicas === null || isNaN(replicas) || replicas < 0)) {
                alert('Valid replica count is required for scale operation');
                return;
            }

            closeModal();
            await this.manageStatefulSet(statefulSetName, selectedOperation, replicas, namespace);
        });
    }

    async manageStatefulSet(statefulSetName, operationType, replicas = null, namespace = null) {
        const operationTypeEnum = operationType === 'rollout' ? 0 : 1; // Rollout = 0, Scale = 1
        const operationLabel = operationType === 'rollout' ? 'Rollout restart' : `Scale to ${replicas} replicas`;
        const namespaceText = namespace ? ` in namespace ${namespace}` : '';

        const toastId = this.showToast(
            `${operationLabel} for StatefulSet '${statefulSetName}'${namespaceText}...`,
            'info',
            'StatefulSet Management',
            0,
            true
        );

        try {
            const requestBody = {
                statefulSetName: statefulSetName,
                namespace: namespace,
                operationType: operationTypeEnum
            };

            if (operationType === 'scale') {
                requestBody.replicas = replicas;
            }

            const response = await fetch(this.manageStatefulSetEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            this.removeToast(toastId);

            if (response.ok) {
                this.showToast(result.message || `StatefulSet operation completed successfully`, 'success', 'Success', 5000);
                // Refresh after a delay to allow operation to take effect
                setTimeout(() => this.refresh(), 3000);
            } else {
                this.showToast(result.error || result.details || 'Failed to manage StatefulSet', 'error', 'Operation Failed', 15000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error managing StatefulSet: ${error.message}`, 'error', 'Error', 15000);
        }
    }

    // ========== Logs Methods ==========

    setupLogsControls() {
        // Close button
        const closeButton = document.getElementById('closeLogsPanel');
        if (closeButton) {
            closeButton.addEventListener('click', () => {
                this.closeLogsPanel();
            });
        }

        // Auto-refresh interval selector
        const intervalSelect = document.getElementById('logsRefreshInterval');
        if (intervalSelect) {
            intervalSelect.addEventListener('change', (e) => {
                const newInterval = parseInt(e.target.value);
                this.logsRefreshInterval = newInterval;
                this.stopLogsAutoRefresh();
                if (newInterval > 0 && this.currentLogContext) {
                    this.startLogsAutoRefresh();
                }
            });
        }

        // Manual refresh button
        const manualRefreshBtn = document.getElementById('logsManualRefresh');
        if (manualRefreshBtn) {
            manualRefreshBtn.addEventListener('click', () => {
                if (this.currentLogContext) {
                    this.refreshLogs();
                }
            });
        }
    }

    openQdrantLogs(podName, namespace, nodeUrl) {
        // Open panel first
        this.openLogsPanel();

        // Check if pod name is available
        if (!podName) {
            this.currentLogContext = null;
            
            const title = document.getElementById('logsPanelTitle');
            if (title) {
                title.innerHTML = `<i class="fas fa-file-alt"></i> Logs: ${nodeUrl || 'Unknown Node'}`;
            }

            const content = document.getElementById('logsPanelContent');
            if (content) {
                content.innerHTML = `<div class="logs-error">
                    <i class="fas fa-exclamation-triangle"></i> 
                    <strong>Cannot view logs</strong><br><br>
                    Pod information is not available for this node.<br>
                    This usually means the node is not running in a Kubernetes cluster or pod metadata is missing.
                </div>`;
            }
            return;
        }

        this.currentLogContext = {
            type: 'qdrant',
            podName: podName,
            namespace: namespace || 'qdrant'
        };

        const title = document.getElementById('logsPanelTitle');
        if (title) {
            title.innerHTML = `<i class="fas fa-file-alt"></i> Logs: ${podName}`;
        }

        this.loadLogs();

        // Set default refresh interval to 15 seconds
        this.logsRefreshInterval = 15000;
        const intervalSelect = document.getElementById('logsRefreshInterval');
        if (intervalSelect) {
            intervalSelect.value = '15000';
        }
        this.startLogsAutoRefresh();
    }

    openVigilanteLogs() {
        this.currentLogContext = {
            type: 'vigilante',
            namespace: 'qdrant' // Default namespace
        };

        const title = document.getElementById('logsPanelTitle');
        if (title) {
            title.innerHTML = `<i class="fas fa-file-alt"></i> Vigilante Logs`;
        }

        this.openLogsPanel();
        this.loadLogs();

        // Set default refresh interval to 15 seconds
        this.logsRefreshInterval = 15000;
        const intervalSelect = document.getElementById('logsRefreshInterval');
        if (intervalSelect) {
            intervalSelect.value = '15000';
        }
        this.startLogsAutoRefresh();
    }

    openLogsPanel() {
        const panel = document.getElementById('logsSidePanel');
        if (panel) {
            panel.classList.add('open');
        }
    }

    closeLogsPanel() {
        const panel = document.getElementById('logsSidePanel');
        if (panel) {
            panel.classList.remove('open');
        }
        this.stopLogsAutoRefresh();
        this.currentLogContext = null;
    }

    async loadLogs() {
        const content = document.getElementById('logsPanelContent');
        if (!content || !this.currentLogContext) return;

        try {
            let response;
            const requestBody = {
                namespace: this.currentLogContext.namespace,
                limit: 200
            };

            if (this.currentLogContext.type === 'qdrant') {
                requestBody.podName = this.currentLogContext.podName;
                response = await fetch(this.qdrantLogsEndpoint, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(requestBody)
                });
            } else if (this.currentLogContext.type === 'vigilante') {
                response = await fetch(this.vigilanteLogsEndpoint, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json'
                    },
                    body: JSON.stringify(requestBody)
                });
            }

            if (!response || !response.ok) {
                throw new Error(`HTTP ${response.status}: ${response.statusText}`);
            }

            const data = await response.json();
            
            if (data.success && data.logs) {
                this.renderLogs(data.logs);
            } else {
                throw new Error(data.message || 'Failed to load logs');
            }
        } catch (error) {
            console.error('Error loading logs:', error);
            content.innerHTML = `<div class="logs-error">
                <i class="fas fa-exclamation-triangle"></i> 
                Failed to load logs: ${error.message}
            </div>`;
        }
    }

    renderLogs(logs) {
        const content = document.getElementById('logsPanelContent');
        if (!content) return;

        if (!logs || logs.length === 0) {
            content.innerHTML = '<div class="logs-empty">No logs available</div>';
            return;
        }

        // Reverse logs to show newest first
        const sortedLogs = [...logs].reverse();

        let html = '';
        sortedLogs.forEach(log => {
            const source = log.source || 'unknown';
            const message = this.escapeHtml(log.message);
            
            html += `<div class="log-entry">
                <div class="log-meta">
                    <span class="log-source">[${source}]</span>
                </div>
                <div class="log-message">${message}</div>
            </div>`;
        });

        content.innerHTML = html;
        
        // Auto-scroll to top (newest logs)
        content.scrollTop = 0;
    }

    escapeHtml(text) {
        const map = {
            '&': '&amp;',
            '<': '&lt;',
            '>': '&gt;',
            '"': '&quot;',
            "'": '&#039;'
        };
        return text.replace(/[&<>"']/g, m => map[m]);
    }

    refreshLogs() {
        const refreshBtn = document.getElementById('logsManualRefresh');
        if (refreshBtn) {
            refreshBtn.classList.add('refreshing');
        }

        this.loadLogs().finally(() => {
            if (refreshBtn) {
                setTimeout(() => {
                    refreshBtn.classList.remove('refreshing');
                }, 1000);
            }
        });
    }

    startLogsAutoRefresh() {
        this.stopLogsAutoRefresh();
        if (this.logsRefreshInterval > 0) {
            this.logsRefreshTimer = setInterval(() => {
                this.loadLogs();
            }, this.logsRefreshInterval);
        }
    }

    stopLogsAutoRefresh() {
        if (this.logsRefreshTimer) {
            clearInterval(this.logsRefreshTimer);
            this.logsRefreshTimer = null;
        }
    }

    // Configuration Management
    setupConfigControls() {
        const configModal = document.getElementById('configModal');
        const closeConfigModal = document.getElementById('closeConfigModal');
        const cancelConfigBtn = document.getElementById('cancelConfig');
        const saveConfigBtn = document.getElementById('saveConfig');

        // Close modal
        const closeModal = () => {
            configModal.style.display = 'none';
        };

        closeConfigModal?.addEventListener('click', closeModal);
        cancelConfigBtn?.addEventListener('click', closeModal);

        // Close on outside click
        configModal?.addEventListener('click', (e) => {
            if (e.target === configModal) {
                closeModal();
            }
        });

        // Save configuration
        saveConfigBtn?.addEventListener('click', async () => {
            await this.saveConfiguration();
        });

        // Orphaned snapshots toggle
        document.getElementById('snapshotOrphanedEnabled')?.addEventListener('change', (e) => {
            document.getElementById('snapshotOrphanedSection').style.display = e.target.checked ? 'block' : 'none';
        });

        // Schedule toggle
        document.getElementById('snapshotScheduleEnabled')?.addEventListener('change', (e) => {
            document.getElementById('snapshotScheduleSection').style.display = e.target.checked ? 'block' : 'none';
        });

        // Overrides toggle
        document.getElementById('snapshotOverridesEnabled')?.addEventListener('change', (e) => {
            document.getElementById('snapshotOverridesSection').style.display = e.target.checked ? 'block' : 'none';
        });

        // Add override row button
        document.getElementById('addOverrideBtn')?.addEventListener('click', () => {
            this.addOverrideRow();
        });

        // S3 enabled toggle
        document.getElementById('s3Enabled')?.addEventListener('change', (e) => {
            document.getElementById('s3Section').style.display = e.target.checked ? 'block' : 'none';
        });
    }

    _escCollectionOpt(s) {
        return String(s)
            .replace(/&/g, '&amp;')
            .replace(/</g, '&lt;')
            .replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;');
    }

    /** Full <select> inner HTML; selectedValue kept selected after refresh (orphan option if not in API list). */
    _buildCollectionSelectInnerHtml(selectedValue) {
        const esc = (x) => this._escCollectionOpt(x);
        const api = this._configCollectionNames || [];
        const inApi = selectedValue && api.includes(selectedValue);
        let html = '<option value="">— select collection —</option>';
        if (selectedValue && !inApi) {
            const e = esc(selectedValue);
            html += `<option value="${e}" selected>${e}</option>`;
        }
        for (const n of api) {
            const sel = n === selectedValue ? ' selected' : '';
            const e = esc(n);
            html += `<option value="${e}"${sel}>${e}</option>`;
        }
        return html;
    }

    _refreshOverrideCollectionPicks() {
        document.querySelectorAll('.override-row .override-collection-name').forEach((sel) => {
            const v = sel.value;
            sel.innerHTML = this._buildCollectionSelectInnerHtml(v);
        });
    }

    _toTimeInputValue(startAt) {
        if (!startAt) return '';
        const d = new Date(startAt);
        if (Number.isNaN(d.getTime())) return '';
        const hh = String(d.getUTCHours()).padStart(2, '0');
        const mm = String(d.getUTCMinutes()).padStart(2, '0');
        return `${hh}:${mm}`;
    }

    _timeInputToIsoUtc(value) {
        if (!value) return null;
        const m = /^(\d{1,2}):(\d{2})(?::(\d{2}))?$/.exec(String(value).trim());
        if (!m) return null;
        const h = Number(m[1]);
        const min = Number(m[2]);
        const sec = m[3] ? Number(m[3]) : 0;
        if (h < 0 || h > 23 || min < 0 || min > 59 || sec < 0 || sec > 59) return null;
        return new Date(Date.UTC(1970, 0, 1, h, min, sec)).toISOString();
    }

    addOverrideRow(name = '', schedule = { enabled: true, intervalMinutes: null, retainLastN: null, startAt: null }) {
        const row = document.createElement('div');
        row.className = 'override-row';
        row.innerHTML = `
            <select class="override-input override-collection-name override-collection-pick" aria-label="Collection">${this._buildCollectionSelectInnerHtml(name)}</select>
            <div class="override-cell-center">
                <input type="checkbox" class="override-checkbox override-enabled" ${schedule.enabled ? 'checked' : ''}>
            </div>
            <input type="number" class="override-input override-interval" placeholder="—" min="1" value="${schedule.intervalMinutes ?? ''}">
            <input type="number" class="override-input override-retain" placeholder="—" min="1" value="${schedule.retainLastN ?? ''}">
            <input type="time" class="override-input override-start-at" step="60" value="${this._toTimeInputValue(schedule.startAt)}">
            <button type="button" class="btn-remove-override" title="Remove"><i class="fas fa-times"></i></button>
        `;
        row.querySelector('.btn-remove-override').addEventListener('click', () => row.remove());
        document.getElementById('overrideRows').appendChild(row);
    }

    async openConfigModal() {
        const configModal = document.getElementById('configModal');
        if (configModal) {
            configModal.style.display = 'flex';
            await this.loadConfiguration();
        }
    }

    _setConfigModalLoading(loading) {
        const modalContent = document.querySelector('#configModal .modal-content');
        if (!modalContent) return;
        if (loading) {
            modalContent.classList.add('config-loading');
            if (!modalContent.querySelector('.config-loading-overlay')) {
                const overlay = document.createElement('div');
                overlay.className = 'config-loading-overlay';
                overlay.innerHTML = '<div class="config-loading-spinner"><i class="fas fa-spinner fa-spin"></i></div>';
                modalContent.appendChild(overlay);
            }
        } else {
            modalContent.classList.remove('config-loading');
            modalContent.querySelector('.config-loading-overlay')?.remove();
        }
    }

    async loadConfiguration() {
        this._setConfigModalLoading(true);
        try {
            const configResponse = await fetch('/api/v1/config');

            if (!configResponse.ok) {
                throw new Error(`HTTP ${configResponse.status}: ${configResponse.statusText}`);
            }

            const config = await configResponse.json();

            // Load collections for override row selects in background
            fetch('/api/v1/collections/info?clearCache=false')
                .then(r => r.ok ? r.json() : null)
                .then(data => {
                    if (!data) return;
                    const names = [...new Set((data.collections || []).map(c => c.collectionName).filter(Boolean))];
                    this._configCollectionNames = names;
                    this._refreshOverrideCollectionPicks();
                })
                .catch(() => {});

            const snap = config.snapshot || {};
            const schedule = snap.schedule || {};
            const overrides = snap.collectionOverrides;

            // Monitoring
            document.getElementById('monitoringInterval').value = config.monitoringIntervalSeconds || 120;
            document.getElementById('diskUsageAlertThresholdPercent').value = config.diskUsageAlertThresholdPercent ?? 90;

            // DeleteWithCollection (default true)
            document.getElementById('snapshotPendingCreateTimeoutSeconds').value = snap.pendingCreateTimeoutSeconds ?? 1800;
            document.getElementById('snapshotDeleteWithCollection').checked = snap.deleteWithCollection !== false;

            // Orphaned
            const orphanEnabled = snap.deleteOrphanedAfterMinutes != null;
            document.getElementById('snapshotOrphanedEnabled').checked = orphanEnabled;
            document.getElementById('snapshotOrphanedSection').style.display = orphanEnabled ? 'block' : 'none';
            document.getElementById('snapshotOrphanedAfterMinutes').value = orphanEnabled ? (snap.deleteOrphanedAfterMinutes ?? '') : '';

            // Schedule
            document.getElementById('snapshotScheduleEnabled').checked = !!schedule.enabled;
            document.getElementById('snapshotScheduleSection').style.display = schedule.enabled ? 'block' : 'none';
            document.getElementById('snapshotScheduleInterval').value = schedule.intervalMinutes ?? '';
            document.getElementById('snapshotScheduleRetain').value = schedule.retainLastN ?? '';
            document.getElementById('snapshotScheduleStartAt').value = this._toTimeInputValue(schedule.startAt);

            // Collection overrides
            const overridesEnabled = overrides != null;
            document.getElementById('snapshotOverridesEnabled').checked = overridesEnabled;
            document.getElementById('snapshotOverridesSection').style.display = overridesEnabled ? 'block' : 'none';
            const overrideRowsEl = document.getElementById('overrideRows');
            overrideRowsEl.innerHTML = '';
            if (overridesEnabled && overrides) {
                for (const [name, sched] of Object.entries(overrides)) {
                    this.addOverrideRow(name, sched);
                }
            }

            // S3 Storage
            const s3 = config.s3 || {};
            const s3Enabled = s3.enabled !== false; // default true
            document.getElementById('s3Enabled').checked = s3Enabled;
            document.getElementById('s3Section').style.display = s3Enabled ? 'block' : 'none';
            document.getElementById('s3BucketName').value = s3.bucketName || '';
            document.getElementById('s3Region').value = s3.region || 'default';

        } catch (error) {
            console.error('Failed to load configuration:', error);
            this.showToast(`Failed to load configuration: ${error.message}`, 'error');
        } finally {
            this._setConfigModalLoading(false);
        }
    }

    async saveConfiguration() {
        const saveConfigBtn = document.getElementById('saveConfig');

        // Monitoring interval
        const monitoringInterval = parseInt(document.getElementById('monitoringInterval').value);
        if (isNaN(monitoringInterval) || monitoringInterval < 1 || monitoringInterval > 3600) {
            this.showToast('Monitoring interval must be between 1 and 3600 seconds', 'error');
            return;
        }

        const diskUsageAlertThresholdPercent = parseFloat(document.getElementById('diskUsageAlertThresholdPercent').value);
        if (isNaN(diskUsageAlertThresholdPercent) || diskUsageAlertThresholdPercent < 1 || diskUsageAlertThresholdPercent > 100) {
            this.showToast('Disk usage alert threshold must be between 1 and 100%', 'error');
            return;
        }

        // Orphaned cleanup
        const pendingTimeoutSeconds = parseInt(document.getElementById('snapshotPendingCreateTimeoutSeconds').value);
        if (isNaN(pendingTimeoutSeconds) || pendingTimeoutSeconds < 1 || pendingTimeoutSeconds > 86400) {
            this.showToast('Pending snapshot timeout must be between 1 and 86400 seconds', 'error');
            return;
        }

        const orphanEnabled = document.getElementById('snapshotOrphanedEnabled').checked;
        const orphanMinutesRaw = document.getElementById('snapshotOrphanedAfterMinutes').value;
        const orphanMinutes = orphanEnabled && orphanMinutesRaw ? parseInt(orphanMinutesRaw) : null;
        if (orphanEnabled && (isNaN(orphanMinutes) || orphanMinutes < 1)) {
            this.showToast('Orphaned cleanup delay must be at least 1 minute', 'error');
            return;
        }

        // Global schedule
        const scheduleEnabled = document.getElementById('snapshotScheduleEnabled').checked;
        const intervalRaw = document.getElementById('snapshotScheduleInterval').value;
        const retainRaw = document.getElementById('snapshotScheduleRetain').value;
        const startAtRaw = document.getElementById('snapshotScheduleStartAt').value;
        const intervalMinutes = intervalRaw ? parseInt(intervalRaw) : null;
        const retainLastN = retainRaw ? parseInt(retainRaw) : null;
        const startAt = this._timeInputToIsoUtc(startAtRaw);

        // Collection overrides
        const overridesEnabled = document.getElementById('snapshotOverridesEnabled').checked;
        let collectionOverrides = null;
        if (overridesEnabled) {
            collectionOverrides = {};
            let overrideError = null;
            document.querySelectorAll('#overrideRows .override-row').forEach(row => {
                const name = row.querySelector('.override-collection-name').value.trim();
                if (!name) {
                    overrideError = 'All override rows must have a collection name';
                    return;
                }
                const iRaw = row.querySelector('.override-interval').value;
                const rRaw = row.querySelector('.override-retain').value;
                const sRaw = row.querySelector('.override-start-at').value;
                collectionOverrides[name] = {
                    enabled: row.querySelector('.override-enabled').checked,
                    intervalMinutes: iRaw ? parseInt(iRaw) : null,
                    retainLastN: rRaw ? parseInt(rRaw) : null,
                    startAt: this._timeInputToIsoUtc(sRaw),
                };
            });
            if (overrideError) {
                this.showToast(overrideError, 'error');
                return;
            }
        }

        // S3 Storage
        const s3Enabled = document.getElementById('s3Enabled').checked;
        const s3BucketName = document.getElementById('s3BucketName').value.trim() || null;
        const s3Region = document.getElementById('s3Region').value.trim() || 'default';

        if (s3Enabled && !s3BucketName) {
            this.showToast('S3 bucket name is required when S3 is enabled', 'error');
            document.getElementById('s3BucketName').focus();
            return;
        }

        saveConfigBtn.disabled = true;
        saveConfigBtn.innerHTML = '<i class="fas fa-spinner fa-spin"></i> Saving...';

        try {
            const response = await fetch('/api/v1/config', {
                method: 'PUT',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    monitoringIntervalSeconds: monitoringInterval,
                    diskUsageAlertThresholdPercent: diskUsageAlertThresholdPercent,
                    snapshot: {
                        pendingCreateTimeoutSeconds: pendingTimeoutSeconds,
                        deleteWithCollection: document.getElementById('snapshotDeleteWithCollection').checked,
                        deleteOrphanedAfterMinutes: orphanMinutes,
                        schedule: {
                            enabled: scheduleEnabled,
                            intervalMinutes: intervalMinutes,
                            retainLastN: retainLastN,
                            startAt: startAt
                        },
                        collectionOverrides: collectionOverrides
                    },
                    s3: {
                        enabled: s3Enabled,
                        bucketName: s3BucketName,
                        region: s3Region
                    }
                })
            });

            if (!response.ok) {
                const errorData = await response.json().catch(() => ({}));
                throw new Error(errorData.error || `HTTP ${response.status}: ${response.statusText}`);
            }

            await response.json();
            this.showToast('Configuration updated successfully!', 'success');
            await this.loadConfiguration();

        } catch (error) {
            console.error('Failed to save configuration:', error);
            this.showToast(`Failed to save configuration: ${error.message}`, 'error');
        } finally {
            saveConfigBtn.disabled = false;
            saveConfigBtn.innerHTML = '<i class="fas fa-save"></i> Save Changes';
        }
    }

    setupStickyActionsMenu() {
        const menuButton = document.getElementById('stickyActionsMenuButton');
        const dropdown = document.getElementById('stickyActionsDropdown');
        const configAction = document.getElementById('stickyConfigAction');
        const logsAction = document.getElementById('stickyLogsAction');

        if (!menuButton || !dropdown) {
            console.warn('Sticky actions menu elements not found');
            return;
        }

        // Toggle menu on button click
        menuButton.addEventListener('click', (e) => {
            e.stopPropagation();
            const wasOpen = dropdown.classList.contains('show');
            
            if (wasOpen) {
                dropdown.classList.remove('show');
                menuButton.classList.remove('active');
                this.stickyActionsMenuOpen = false;
            } else {
                dropdown.classList.add('show');
                menuButton.classList.add('active');
                this.stickyActionsMenuOpen = true;
            }
        });

        // Configuration action
        configAction?.addEventListener('click', async (e) => {
            e.stopPropagation();
            dropdown.classList.remove('show');
            menuButton.classList.remove('active');
            this.stickyActionsMenuOpen = false;
            
            // Open config modal directly
            await this.openConfigModal();
        });

        // View Logs action
        logsAction?.addEventListener('click', (e) => {
            e.stopPropagation();
            dropdown.classList.remove('show');
            menuButton.classList.remove('active');
            this.stickyActionsMenuOpen = false;
            
            // Open Vigilante logs directly
            this.openVigilanteLogs();
        });

        // Close dropdown when clicking outside
        document.addEventListener('click', (e) => {
            if (!menuButton.contains(e.target) && !dropdown.contains(e.target)) {
                dropdown.classList.remove('show');
                menuButton.classList.remove('active');
                this.stickyActionsMenuOpen = false;
            }
        });
    }
}

// Initialize dashboard when page loads and store it globally
let dashboard = null;
document.addEventListener('DOMContentLoaded', () => {
    console.log('DOM loaded, initializing dashboard');
    dashboard = new VigilanteDashboard();
    window.dashboard = dashboard; // Store for debugging
});

// Close the topmost modal on Escape key press
document.addEventListener('keydown', (e) => {
    if (e.key !== 'Escape') return;

    // Dynamic modals (created via JS, use .modal-overlay)
    const overlays = document.querySelectorAll('.modal-overlay');
    if (overlays.length > 0) {
        overlays[overlays.length - 1].click();
        return;
    }

    // Static modals (defined in HTML, use .modal with display:flex/block or .show class)
    const staticModals = document.querySelectorAll('.modal');
    for (let i = staticModals.length - 1; i >= 0; i--) {
        const m = staticModals[i];
        const isVisible = (m.style.display && m.style.display !== 'none') || m.classList.contains('show');
        if (isVisible) {
            m.click();
            return;
        }
    }

    // Logs side panel
    if (dashboard) {
        const logsPanel = document.getElementById('logsSidePanel');
        if (logsPanel && logsPanel.classList.contains('open')) {
            dashboard.closeLogsPanel();
        }
    }
});

// Handle page visibility changes to pause/resume auto-refresh
document.addEventListener('visibilitychange', () => {
    if (dashboard) {
        if (document.hidden) {
            dashboard.stopAutoRefresh();
        } else if (dashboard.refreshInterval > 0) {
            // Only restart auto-refresh if it was enabled before
            dashboard.startAutoRefresh();
        }
    }
});

