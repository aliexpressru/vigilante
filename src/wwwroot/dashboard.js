class VigilanteDashboard {
    constructor() {
        this.statusApiEndpoint = '/api/v1/cluster/status';
        this.sizesPaginatedApiEndpoint = '/api/v1/collections/info';
        this.snapshotsApiEndpoint = '/api/v1/snapshots/info';
        this.replicateShardsEndpoint = '/api/v1/cluster/replicate-shards';
        this.deleteCollectionEndpoint = '/api/v1/collections';
        this.createSnapshotEndpoint = '/api/v1/snapshots';
        this.deleteSnapshotEndpoint = '/api/v1/snapshots';
        this.downloadSnapshotEndpoint = '/api/v1/snapshots/download';
        this.recoverFromSnapshotEndpoint = '/api/v1/snapshots/recover';
        this.deletePodEndpoint = '/api/v1/kubernetes/delete-pod';
        this.manageStatefulSetEndpoint = '/api/v1/kubernetes/manage-statefulset';
        this.qdrantLogsEndpoint = '/api/v1/logs/qdrant';
        this.vigilanteLogsEndpoint = '/api/v1/logs/vigilante';
        this.refreshInterval = 0;
        this.autoRefreshTimer = null;
        this.openSnapshots = new Set();
        this.selectedState = new Map();
        this.toastIdCounter = 0; // Counter for unique toast IDs
        this.clusterIssues = []; // Issues from cluster/status
        this.collectionIssues = []; // Issues from collections-info
        this.clusterNodes = []; // Store cluster nodes for StatefulSet management
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
        this.init();
        this.setupRefreshControls();
        this.setupCollectionControls();
        this.setupSnapshotControls();
        this.setupLogsControls();
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

    setupRefreshControls() {
        const intervalSelect = document.getElementById('refreshInterval');
        const manualRefreshBtn = document.getElementById('manualRefresh');

        // Read initial value from HTML (15s by default)
        const initialInterval = parseInt(intervalSelect.value || '0');
        this.refreshInterval = initialInterval;
        
        // Start auto-refresh if interval is set
        if (initialInterval > 0) {
            console.log(`Starting auto-refresh with initial interval: ${initialInterval}ms`);
            this.startAutoRefresh();
        }

        // Handle interval changes
        intervalSelect.addEventListener('change', (e) => {
            const newInterval = parseInt(e.target.value);
            this.refreshInterval = newInterval;
            
            this.stopAutoRefresh();
            if (newInterval > 0) {
                this.startAutoRefresh();
            }
        });

        // Handle manual refresh
        manualRefreshBtn.addEventListener('click', () => {
            this.refresh();
        });
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

    openRecoveryModal(sourceSnapshot, originalCollectionName, snapshotName) {
        console.log('openRecoveryModal called with:', { sourceSnapshot, originalCollectionName, snapshotName });
        
        const modal = document.getElementById('recoveryModal');
        const targetNodeSelect = document.getElementById('recoverTargetNode');
        const targetNodeFormGroup = targetNodeSelect.closest('.form-group');
        const collectionNameInput = document.getElementById('recoverCollectionName');
        const sourceSnapshotInput = document.getElementById('recoverSourceSnapshot');

        console.log('Modal form elements:', { modal, targetNodeSelect, collectionNameInput, sourceSnapshotInput });

        // Set source snapshot display
        sourceSnapshotInput.value = snapshotName;

        // Check if snapshot has a specific node URL (Kubernetes storage)
        const snapshotNodeUrl = sourceSnapshot.nodeUrl;
        const hasFixedNode = snapshotNodeUrl && snapshotNodeUrl !== 'unknown';
        
        if (hasFixedNode) {
            // For Kubernetes storage - use the node where snapshot was created
            // Hide node selector and auto-populate
            targetNodeFormGroup.style.display = 'none';
            targetNodeSelect.innerHTML = `<option value="${snapshotNodeUrl}" selected>Auto-selected: ${sourceSnapshot.podName || snapshotNodeUrl}</option>`;
            console.log('Auto-selected target node (Kubernetes storage):', snapshotNodeUrl);
        } else {
            // For S3/other sources - show node selector
            targetNodeFormGroup.style.display = 'block';
            targetNodeSelect.innerHTML = '<option value="">Select target node...</option>';
            if (this.clusterNodes && this.clusterNodes.length > 0) {
                this.clusterNodes.forEach(node => {
                    const option = document.createElement('option');
                    option.value = node.nodeUrl || node.url;
                    
                    // Build display text: prefer podName, fallback to URL and peer ID
                    let displayText = '';
                    if (node.podName && node.podName !== 'unknown') {
                        displayText = node.podName;
                        if (node.peerId) {
                            displayText += ` (${node.peerId.substring(0, 12)}...)`;
                        }
                    } else {
                        // Use URL and peer ID
                        const url = node.nodeUrl || node.url || '';
                        const peerId = node.peerId ? node.peerId.substring(0, 12) + '...' : '';
                        displayText = peerId ? `${url} (${peerId})` : url;
                    }
                    
                    option.textContent = displayText;
                    targetNodeSelect.appendChild(option);
                });
            }
        }

        // Set default collection name (editable)
        collectionNameInput.value = originalCollectionName;

        // Store recovery context
        this.recoveryContext = {
            sourceSnapshot,
            originalCollectionName,
            snapshotName,
            source: sourceSnapshot.source || 'QdrantApi' // Default to QdrantApi if not specified
        };

        console.log('Recovery context stored:', this.recoveryContext);
        console.log('Cluster nodes available:', this.clusterNodes);

        // Show modal
        modal.classList.add('show');
        console.log('Modal shown, classList:', modal.classList);
    }

    closeRecoveryModal() {
        const modal = document.getElementById('recoveryModal');
        modal.classList.remove('show');
        this.recoveryContext = null;
    }

    async confirmRecovery() {
        console.log('confirmRecovery called');
        
        const targetNodeSelect = document.getElementById('recoverTargetNode');
        const collectionNameInput = document.getElementById('recoverCollectionName');

        const targetNodeUrl = targetNodeSelect.value;
        const collectionName = collectionNameInput.value.trim();

        console.log('Recovery params:', { targetNodeUrl, collectionName, recoveryContext: this.recoveryContext });

        // Validation
        if (!targetNodeUrl) {
            this.showToast('Please select a target node', 'error', null, 15000);
            return;
        }

        if (!collectionName) {
            this.showToast('Please enter a collection name', 'error', null, 15000);
            return;
        }

        if (!this.recoveryContext) {
            this.showToast('Recovery context lost', 'error', null, 15000);
            return;
        }

        // Get data from recovery context BEFORE closing modal
        const { snapshotName, source, originalCollectionName } = this.recoveryContext;
        const targetNode = this.clusterNodes.find(n => (n.nodeUrl || n.url) === targetNodeUrl);
        const podName = targetNode ? targetNode.podName : null;

        console.log('Calling recoverSnapshotFromNode with:', { 
            targetNodeUrl, 
            collectionName, 
            snapshotName, 
            podName, 
            source,
            originalCollectionName 
        });

        // Close modal AFTER getting all data
        this.closeRecoveryModal();
        
        await this.recoverSnapshotFromNode(
            targetNodeUrl, 
            collectionName, 
            snapshotName, 
            podName, 
            source,
            originalCollectionName  // Pass original collection name for S3 lookups
        );
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
            const shardsHtml = value.map(shardId => {
                // Get the state for this shard from nodeInfo metrics
                const shardStates = nodeInfo?.metrics?.shardStates || {};
                const state = shardStates[shardId.toString()] || 'Unknown';
                const stateClass = state.toLowerCase().replace(/\s+/g, '-');
                
                return `
                    <div class="shard-item">
                        <input type="checkbox" class="shard-checkbox" data-shard-id="${shardId}" id="shard_${shardId}">
                        <label for="shard_${shardId}" class="shard-label">
                            <span class="shard-id">Shard ${shardId}</span>
                            <span class="shard-state ${stateClass}">${state}</span>
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
                    <div class="action-controls">
                        <button class="replicate-button">Sync</button>
                    </div>
                </div>
            `;
        }
        if (key === 'outgoingTransfers') {
            if (!Array.isArray(value) || value.length === 0) return '';
            return value.map(transfer => {
                const transferType = transfer.isSync ? 'Syncing' : 'Moving';
                return `<div class="transfer-item">${transferType} shard ${transfer.shardId} → ${transfer.to}</div>`;
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
        
        // Remember which collections were open before update
        const openCollections = new Set();
        document.querySelectorAll('.collection-details.visible').forEach(row => {
            const nameCell = row.previousElementSibling.querySelector('.collection-name-line');
            if (nameCell) {
                openCollections.add(nameCell.textContent.trim());
            }
        });
        
        console.log('Saving open collections state:', Array.from(openCollections));
        
        // Group collections by name and sort them
        const collectionsByName = collections.reduce((acc, info) => {
            if (!info || !info.collectionName) {
                console.warn('Invalid collection info:', info);
                return acc;
            }

            if (!acc[info.collectionName]) {
                acc[info.collectionName] = {
                    name: info.collectionName,
                    aliases: info.aliases || [],
                    nodes: {}
                };
            }

            // Use peerId as unique key to avoid overwriting nodes with the same podName
            const nodeKey = info.peerId || info.podName || info.nodeUrl;
            acc[info.collectionName].nodes[nodeKey] = {
                size: info.metrics?.size || 0,
                podName: info.podName,
                peerId: info.peerId || '',
                nodeUrl: info.nodeUrl || '',
                podNamespace: info.podNamespace || '',
                metrics: info.metrics || {}
            };
            return acc;
        }, {});
        
        console.log('Collections grouped by name:', collectionsByName);
        console.log('Total unique collections:', Object.keys(collectionsByName).length);
        Object.entries(collectionsByName).forEach(([name, collection]) => {
            console.log(`Collection ${name} has ${Object.keys(collection.nodes).length} nodes`);
        });

        // Get unique node keys (peerIds or podNames) for display
        const nodeKeys = [...new Set(collections.map(info => info.peerId || info.podName || info.nodeUrl).filter(Boolean))].sort();
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
                Object.values(collection.nodes).forEach(nodeInfo => {
                    if (nodeInfo.metrics?.sizeBytes) {
                        collectionTotalSize += nodeInfo.metrics.sizeBytes;
                    }
                    // Collect unique shard IDs from all nodes
                    if (nodeInfo.metrics?.shards && Array.isArray(nodeInfo.metrics.shards)) {
                        nodeInfo.metrics.shards.forEach(shardId => uniqueShards.add(shardId));
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
                
                // Collection name
                const nameDiv = document.createElement('div');
                nameDiv.className = 'collection-name-line';
                nameDiv.textContent = collection.name;
                nameDiv.title = collection.name;
                nameContainer.appendChild(nameDiv);
                
                // Aliases (if any)
                if (collection.aliases && collection.aliases.length > 0) {
                    const aliasesDiv = document.createElement('div');
                    aliasesDiv.className = 'collection-aliases';
                    aliasesDiv.style.fontSize = '0.85em';
                    aliasesDiv.style.color = '#999';
                    aliasesDiv.style.fontStyle = 'italic';
                    aliasesDiv.textContent = `Aliases: ${collection.aliases.join(', ')}`;
                    aliasesDiv.title = `Collection aliases: ${collection.aliases.join(', ')}`;
                    nameContainer.appendChild(aliasesDiv);
                }
                
                // Right side: Size and shards info
                const infoContainer = document.createElement('div');
                infoContainer.style.display = 'flex';
                infoContainer.style.flexDirection = 'column';
                infoContainer.style.alignItems = 'flex-end';
                infoContainer.style.gap = '2px';
                
                // Size span
                const sizeSpan = document.createElement('span');
                sizeSpan.className = 'collection-size';
                sizeSpan.textContent = this.formatSize(collectionTotalSize);
                infoContainer.appendChild(sizeSpan);
                
                // Shards count (if any)
                if (collectionTotalShards > 0) {
                    const shardsSpan = document.createElement('span');
                    shardsSpan.className = 'collection-shards-count';
                    shardsSpan.style.fontSize = '0.85em';
                    shardsSpan.style.color = '#666';
                    shardsSpan.innerHTML = `<i class="fas fa-layer-group"></i> ${collectionTotalShards} ${collectionTotalShards === 1 ? 'shard' : 'shards'}`;
                    shardsSpan.title = `Unique shards in collection: ${collectionTotalShards}`;
                    infoContainer.appendChild(shardsSpan);
                }
                
                headerContainer.appendChild(nameContainer);
                headerContainer.appendChild(infoContainer);
                nameCell.appendChild(headerContainer);
                row.appendChild(nameCell);

                const detailsRow = document.createElement('tr');
                detailsRow.className = 'collection-details';
                const shouldBeVisible = openCollections.has(collection.name);
                if (shouldBeVisible) {
                    detailsRow.classList.add('visible');
                    console.log(`Restoring visible state for collection: ${collection.name}`);
                }

                const detailsCell = document.createElement('td');
                detailsCell.colSpan = nodeKeys.length + 1;
                const detailsContent = document.createElement('div');
                detailsContent.className = 'collection-details-content';

                nodeKeys.forEach(nodeKey => {
                    const nodeInfo = collection.nodes[nodeKey];
                    if (nodeInfo) {
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
                                        <dt>Transfers:</dt>
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

                        // Setup replicate button
                        const replicateButton = nodeDetails.querySelector('.replicate-button');
                        if (replicateButton) {
                            replicateButton.addEventListener('click', () => {
                                const selectedShards = Array.from(
                                    nodeDetails.querySelectorAll('.shard-checkbox:checked')
                                ).map(cb => parseInt(cb.dataset.shardId));

                                if (selectedShards.length === 0) {
                                    alert('Please select at least one shard to sync');
                                    return;
                                }

                                // Open shard sync modal
                                this.showShardSyncModal(collection, nodeInfo, selectedShards);
                            });
                        }

                        detailsContent.appendChild(nodeDetails);
                    }
                });

                // Add collection-level action buttons at the bottom
                const actionsFooter = document.createElement('div');
                actionsFooter.className = 'collection-actions-footer';
                actionsFooter.style.padding = '16px';
                actionsFooter.style.backgroundColor = '#f5f5f5';
                actionsFooter.style.borderTop = '2px solid #ddd';
                actionsFooter.style.display = 'flex';
                actionsFooter.style.gap = '8px';
                actionsFooter.style.justifyContent = 'flex-end';
                
                const deleteApiButton = document.createElement('button');
                deleteApiButton.className = 'action-button action-button-danger';
                deleteApiButton.innerHTML = '<i class="fas fa-trash"></i> Delete (API)';
                deleteApiButton.title = 'Delete collection via API on selected nodes';
                deleteApiButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.showNodeSelectionDialog(collection, 'deleteApi');
                };
                
                const deleteDiskButton = document.createElement('button');
                deleteDiskButton.className = 'action-button action-button-danger';
                deleteDiskButton.innerHTML = '<i class="fas fa-trash"></i> Delete (Disk)';
                deleteDiskButton.title = 'Delete collection from disk on selected nodes';
                deleteDiskButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.showNodeSelectionDialog(collection, 'deleteDisk');
                };
                
                const createSnapshotButton = document.createElement('button');
                createSnapshotButton.className = 'action-button action-button-primary';
                createSnapshotButton.innerHTML = '<i class="fas fa-camera"></i> Create Snapshot';
                createSnapshotButton.title = 'Create snapshot on selected nodes';
                createSnapshotButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.showNodeSelectionDialog(collection, 'createSnapshot');
                };
                
                actionsFooter.appendChild(deleteApiButton);
                actionsFooter.appendChild(deleteDiskButton);
                actionsFooter.appendChild(createSnapshotButton);
                detailsContent.appendChild(actionsFooter);

                detailsCell.appendChild(detailsContent);
                detailsRow.appendChild(detailsCell);

                row.addEventListener('click', () => {
                    const wasVisible = detailsRow.classList.contains('visible');
                    console.log(`Collection ${collection.name} clicked, was visible: ${wasVisible}, will be: ${!wasVisible}`);
                    if (wasVisible) {
                        detailsRow.classList.remove('visible');
                    } else {
                        detailsRow.classList.add('visible');
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
            nameDiv.innerHTML = `<i class="fas fa-camera" style="color: #7b1fa2; margin-right: 8px;"></i>${collection.collectionName}`;
            nameDiv.title = collection.collectionName;
            
            const sizeSpan = document.createElement('span');
            sizeSpan.className = 'collection-size';
            sizeSpan.textContent = this.formatSize(collection.totalSize);
            
            headerContainer.appendChild(nameDiv);
            headerContainer.appendChild(sizeSpan);
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
                <th>Actions</th>
            `;
            nodesTable.appendChild(nodesHeader);

            collection.snapshots.forEach(snapshot => {
                const nodeRow = document.createElement('tr');
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
                cellSize.textContent = snapshot.prettySize;
                
                const cellActions = document.createElement('td');
                const actionsContainer = document.createElement('div');
                actionsContainer.className = 'snapshot-actions-cell';
                
                const downloadBtn = document.createElement('button');
                downloadBtn.className = 'action-button action-button-primary action-button-sm';
                downloadBtn.innerHTML = '<i class="fas fa-download"></i>';
                downloadBtn.title = 'Download snapshot (tries API first, then disk fallback)';
                downloadBtn.onclick = () => this.downloadSnapshot(
                    collection.collectionName, 
                    snapshot.snapshotName, 
                    snapshot.nodeUrl,
                    snapshot.podName, 
                    snapshot.podNamespace || 'qdrant',
                    snapshot.source
                );
                
                // Add "Get Download URL" button for S3 snapshots
                if (snapshot.source === 'S3Storage') {
                    const getUrlBtn = document.createElement('button');
                    getUrlBtn.className = 'action-button action-button-info action-button-sm';
                    getUrlBtn.innerHTML = '<i class="fas fa-link"></i>';
                    getUrlBtn.title = 'Get presigned download URL (valid for 1 hour)';
                    getUrlBtn.onclick = () => this.getS3DownloadUrl(
                        collection.collectionName,
                        snapshot.snapshotName
                    );
                    actionsContainer.appendChild(getUrlBtn);
                }
                
                const recoverBtn = document.createElement('button');
                recoverBtn.className = 'action-button action-button-success action-button-sm';
                recoverBtn.innerHTML = '<i class="fas fa-undo"></i>';
                recoverBtn.title = 'Recover from this snapshot';
                recoverBtn.onclick = () => this.openRecoveryModal(snapshot, collection.collectionName, snapshot.snapshotName);
                
                const deleteBtn = document.createElement('button');
                deleteBtn.className = 'action-button action-button-danger action-button-sm';
                deleteBtn.innerHTML = '<i class="fas fa-trash"></i>';
                deleteBtn.title = 'Delete this snapshot';
                deleteBtn.onclick = () => this.deleteSnapshotFromNode(snapshot);
                
                actionsContainer.appendChild(downloadBtn);
                actionsContainer.appendChild(recoverBtn);
                actionsContainer.appendChild(deleteBtn);
                cellActions.appendChild(actionsContainer);
                
                nodeRow.appendChild(cellNode);
                nodeRow.appendChild(cellPeer);
                nodeRow.appendChild(cellPod);
                nodeRow.appendChild(cellSnapshot);
                nodeRow.appendChild(cellSize);
                nodeRow.appendChild(cellActions);
                
                nodesTable.appendChild(nodeRow);
            });

            // Add "Delete All" row at the bottom
            const deleteAllRow = document.createElement('tr');
            deleteAllRow.className = 'delete-all-row';
            const deleteAllCell = document.createElement('td');
            deleteAllCell.colSpan = 6;
            deleteAllCell.style.textAlign = 'right';
            deleteAllCell.style.padding = '12px 8px';
            deleteAllCell.style.backgroundColor = '#f5f5f5';
            deleteAllCell.style.borderTop = '2px solid #ddd';
            
            const deleteAllBtn = document.createElement('button');
            deleteAllBtn.className = 'action-button action-button-danger';
            deleteAllBtn.innerHTML = '<i class="fas fa-trash"></i> Delete All Snapshots';
            deleteAllBtn.title = 'Delete all snapshots for this collection from all nodes';
            deleteAllBtn.onclick = (e) => {
                e.stopPropagation();
                this.deleteSnapshotFromAllNodes(collection);
            };
            
            deleteAllCell.appendChild(deleteAllBtn);
            deleteAllRow.appendChild(deleteAllCell);
            nodesTable.appendChild(deleteAllRow);

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

    async recoverSnapshotFromNode(nodeUrl, collectionName, snapshotName, podName = null, source = 'QdrantApi', sourceCollectionName = null) {
        // Show podName if available and not 'unknown', otherwise show nodeUrl
        const nodeIdentifier = podName && podName !== 'unknown' ? podName : nodeUrl;
        const toastId = this.showToast(`Recovering ${collectionName} from ${snapshotName} on ${nodeIdentifier}...`, 'info', null, 0);
        
        const requestBody = {
            TargetNodeUrl: nodeUrl,
            CollectionName: collectionName,
            SnapshotName: snapshotName,
            Source: source
        };
        
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
                SingleNode: true,
                Source: snapshot.source
            };

            // For S3Storage, we don't need NodeUrl, PodName, or PodNamespace
            // For other sources, add optional fields only if they have valid values
            if (snapshot.source !== 'S3Storage') {
                if (snapshot.nodeUrl && snapshot.nodeUrl.trim() !== '' && snapshot.nodeUrl !== 'S3') {
                    requestBody.NodeUrl = snapshot.nodeUrl;
                    console.log('Added NodeUrl to request:', snapshot.nodeUrl);
                } else {
                    console.log('NodeUrl NOT added - value:', snapshot.nodeUrl);
                }
                
                if (snapshot.podName && snapshot.podName.trim() !== '' && snapshot.podName !== 'S3') {
                    requestBody.PodName = snapshot.podName;
                    console.log('Added PodName to request:', snapshot.podName);
                }
                
                if (snapshot.podNamespace && snapshot.podNamespace.trim() !== '' && snapshot.podNamespace !== 'S3') {
                    requestBody.PodNamespace = snapshot.podNamespace;
                    console.log('Added PodNamespace to request:', snapshot.podNamespace);
                }
            } else {
                console.log('S3Storage snapshot - skipping NodeUrl, PodName, PodNamespace');
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
                    SingleNode: true,
                    Source: snapshot.source
                };

                // For S3Storage, we don't need NodeUrl, PodName, or PodNamespace
                // For other sources, add optional fields only if they have valid values
                if (snapshot.source !== 'S3Storage') {
                    if (snapshot.nodeUrl && snapshot.nodeUrl.trim() !== '' && snapshot.nodeUrl !== 'S3') {
                        requestBody.NodeUrl = snapshot.nodeUrl;
                    }
                    if (snapshot.podName && snapshot.podName.trim() !== '' && snapshot.podName !== 'S3') {
                        requestBody.PodName = snapshot.podName;
                    }
                    if (snapshot.podNamespace && snapshot.podNamespace.trim() !== '' && snapshot.podNamespace !== 'S3') {
                        requestBody.PodNamespace = snapshot.podNamespace;
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
                    <label class="checkbox-label">
                        <input type="checkbox" id="recoverFromUrlWaitForResult" checked />
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
            const waitForResultInput = overlay.querySelector('#recoverFromUrlWaitForResult');
            
            console.log('Form elements:', {
                collectionNameInput,
                snapshotUrlInput,
                snapshotChecksumInput,
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
            const waitForResult = waitForResultInput?.checked ?? true;
            
            console.log('Recover from URL form values:', {
                collectionName,
                collectionNameLength: collectionName.length,
                snapshotUrl,
                snapshotUrlLength: snapshotUrl.length,
                snapshotChecksum,
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
            await this.recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, waitForResult);
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

    async recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, waitForResult) {
        const toastId = this.showToast(
            `Recovering collection '${collectionName}' from URL on node ${nodeUrl}...`, 
            'info',
            null,
            0
        );
        
        try {
            const requestBody = {
                NodeUrl: nodeUrl,
                CollectionName: collectionName,
                SnapshotUrl: snapshotUrl,
                WaitForResult: waitForResult
            };

            // Add SnapshotChecksum only if it has a value
            if (snapshotChecksum && snapshotChecksum.trim() !== '') {
                requestBody.SnapshotChecksum = snapshotChecksum;
            }

            console.log('Recover from URL request:', requestBody);

            const response = await fetch('/api/v1/snapshots/recover-from-url', {
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

        const details = document.createElement('div');
        details.className = 'node-details';

        // Pod name (if available)
        if (node.podName) {
            const podDetailsContainer = document.createElement('div');
            podDetailsContainer.className = 'pod-details-container';
            
            const podDetail = this.createNodeDetail('Pod', node.podName);
            podDetailsContainer.appendChild(podDetail);

            // Add kubectl exec button
            const execButton = document.createElement('button');
            execButton.className = 'kubectl-exec-button';
            execButton.innerHTML = '<i class="fas fa-terminal"></i> Generate exec';
            execButton.addEventListener('click', (e) => {
                e.stopPropagation();
                const command = `kubectl exec -n qdrant -c qdrant --stdin --tty ${node.podName} -- /bin/bash`;
                
                // Create temporary textarea for copying
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
                    
                    // Show success feedback
                    const originalText = execButton.innerHTML;
                    execButton.innerHTML = '<i class="fas fa-check"></i> Copied!';
                    execButton.classList.add('copied');
                    console.log('Command copied:', command);
                    
                    setTimeout(() => {
                        execButton.innerHTML = originalText;
                        execButton.classList.remove('copied');
                    }, 2000);
                } catch (err) {
                    console.error('Failed to copy command:', err);
                    document.body.removeChild(textarea);
                    
                    // Show error feedback
                    const originalText = execButton.innerHTML;
                    execButton.innerHTML = '<i class="fas fa-times"></i> Failed to copy';
                    execButton.style.background = '#f44336';
                    
                    setTimeout(() => {
                        execButton.innerHTML = originalText;
                        execButton.style.background = '';
                    }, 2000);
                }
            });
            podDetailsContainer.appendChild(execButton);
            
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

        // Dashboard button after namespace
        const dashboardBtn = document.createElement('button');
        dashboardBtn.className = 'dashboard-button';
        dashboardBtn.innerHTML = '<i class="fas fa-chart-line"></i> Open Dashboard';
        dashboardBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            const dashboardUrl = new URL(node.url);
            dashboardUrl.pathname = '/dashboard';
            window.open(dashboardUrl.toString(), '_blank');
        });
        details.appendChild(dashboardBtn);

        // View Logs button
        const viewLogsBtn = document.createElement('button');
        viewLogsBtn.className = 'view-logs-button';
        viewLogsBtn.innerHTML = '<i class="fas fa-file-alt"></i> View Logs';
        viewLogsBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.openQdrantLogs(node.podName, node.namespace, node.url);
        });
        details.appendChild(viewLogsBtn);

        // Recover from URL button
        const recoverFromUrlBtn = document.createElement('button');
        recoverFromUrlBtn.className = 'recover-from-url-button';
        recoverFromUrlBtn.innerHTML = '<i class="fas fa-cloud-download-alt"></i> Recover from URL';
        recoverFromUrlBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.showRecoverFromUrlDialog(node.url);
        });
        details.appendChild(recoverFromUrlBtn);

        // Delete Pod button (always show)
        const deletePodBtn = document.createElement('button');
        deletePodBtn.className = 'delete-pod-button';
        deletePodBtn.innerHTML = '<i class="fas fa-trash-alt"></i> Delete Pod';
        deletePodBtn.addEventListener('click', (e) => {
            e.stopPropagation();
            if (!node.podName) {
                alert('Cannot delete pod: Not running in Kubernetes cluster.\n\nPod information is not available.');
                return;
            }
            this.deletePod(node.podName, node.namespace);
        });
        details.appendChild(deletePodBtn);

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
        const refreshIndicator = document.getElementById('refreshIndicator');
        
        // Remove previous animation if it exists
        this.hideRefreshAnimation();
        
        statusCard.classList.add('refreshing');
        refreshButton.classList.add('refreshing');
        refreshIndicator.classList.add('refreshing');

        // Remove animation classes after animation completes
        setTimeout(() => {
            statusCard.classList.remove('refreshing');
        }, 800);
    }

    hideRefreshAnimation() {
        const statusCard = document.getElementById('overallStatus');
        const refreshButton = document.getElementById('manualRefresh');
        const refreshIndicator = document.getElementById('refreshIndicator');
        
        statusCard.classList.remove('refreshing');
        refreshButton.classList.remove('refreshing');
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
        description.textContent = `Select the nodes where you want to ${action === 'createSnapshot' ? 'create snapshot' : 'delete collection'} for "${collection.name}":`;
        description.style.cssText = 'color: #666; word-wrap: break-word; overflow-wrap: break-word; max-width: 100%;';

        const nodesList = document.createElement('div');
        nodesList.style.cssText = 'margin: 16px 0; max-height: 400px; overflow-y: auto;';

        // Get nodes from collection
        const nodes = Object.values(collection.nodes || {});
        
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
        selectAllContainer.style.cssText = 'margin-bottom: 12px; padding-bottom: 12px; border-bottom: 2px solid #eee;';
        const selectAllCheckbox = document.createElement('input');
        selectAllCheckbox.type = 'checkbox';
        selectAllCheckbox.checked = true;
        selectAllCheckbox.id = 'select-all-nodes';
        const selectAllLabel = document.createElement('label');
        selectAllLabel.htmlFor = 'select-all-nodes';
        selectAllLabel.textContent = ' Select All Nodes';
        selectAllLabel.style.cssText = 'font-weight: bold; cursor: pointer; user-select: none;';
        selectAllContainer.appendChild(selectAllCheckbox);
        selectAllContainer.appendChild(selectAllLabel);
        nodesList.appendChild(selectAllContainer);

        // Add node checkboxes
        availableNodes.forEach((node, index) => {
            const nodeContainer = document.createElement('div');
            nodeContainer.style.cssText = 'margin: 8px 0; padding: 8px; background: #f5f5f5; border-radius: 4px;';
            
            const checkbox = document.createElement('input');
            checkbox.type = 'checkbox';
            checkbox.checked = true;
            checkbox.dataset.nodeIndex = index;
            checkbox.id = `node-${index}`;
            checkbox.className = 'node-checkbox';
            
            const label = document.createElement('label');
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
            padding: 24px;
            border-radius: 12px 12px 0 0;
        `;
        
        const title = document.createElement('h3');
        title.textContent = '🔄 Sync Shards';
        title.style.cssText = 'margin: 0; font-size: 24px; font-weight: 600;';
        header.appendChild(title);
        
        // Content area
        const contentArea = document.createElement('div');
        contentArea.style.cssText = 'padding: 24px; overflow-y: auto; max-height: calc(85vh - 200px);';
        
        const sourceDisplay = sourceNodeInfo.podName && sourceNodeInfo.podName !== 'unknown' 
            ? sourceNodeInfo.podName 
            : sourceNodeInfo.peerId;
        
        // Info card
        const infoCard = document.createElement('div');
        infoCard.style.cssText = `
            background: #f8f9fa;
            border-left: 4px solid #667eea;
            padding: 16px;
            border-radius: 6px;
            margin-bottom: 24px;
        `;
        
        infoCard.innerHTML = `
            <div style="display: flex; flex-direction: column; gap: 8px;">
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span style="font-weight: 600; color: #495057; min-width: 100px; flex-shrink: 0;">Collection:</span>
                    <span style="color: #212529; font-family: monospace; background: white; padding: 4px 8px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0;">${collection.name}</span>
                </div>
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span style="font-weight: 600; color: #495057; min-width: 100px; flex-shrink: 0;">Source node:</span>
                    <span style="color: #212529; font-family: monospace; background: white; padding: 4px 8px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0;">${sourceDisplay}</span>
                </div>
                <div style="display: flex; align-items: flex-start; gap: 8px;">
                    <span style="font-weight: 600; color: #495057; min-width: 100px; flex-shrink: 0;">Shards:</span>
                    <span style="color: #212529; font-family: monospace; background: white; padding: 4px 8px; border-radius: 4px; word-wrap: break-word; overflow-wrap: break-word; word-break: break-word; flex: 1; min-width: 0;">${selectedShards.join(', ')}</span>
                </div>
            </div>
        `;
        
        contentArea.appendChild(infoCard);

        // Target node selection
        const targetNodeSection = document.createElement('div');
        targetNodeSection.style.cssText = 'margin-bottom: 24px;';
        
        const targetLabel = document.createElement('label');
        targetLabel.textContent = 'Target Node';
        targetLabel.style.cssText = `
            display: block;
            font-weight: 600;
            margin-bottom: 8px;
            color: #495057;
            font-size: 14px;
            text-transform: uppercase;
            letter-spacing: 0.5px;
        `;
        
        const targetSelect = document.createElement('select');
        targetSelect.style.cssText = `
            width: 100%;
            padding: 12px;
            border: 2px solid #e9ecef;
            border-radius: 8px;
            font-size: 14px;
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
        Object.entries(collection.nodes || {})
            .filter(([_, nodeInfo]) => nodeInfo.peerId && nodeInfo.peerId !== sourceNodeInfo.peerId)
            .forEach(([_, nodeInfo]) => {
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

        // Move checkbox
        const moveSection = document.createElement('div');
        moveSection.style.cssText = `
            background: #fff3cd;
            border: 2px solid #ffc107;
            border-radius: 8px;
            padding: 16px;
            margin-bottom: 24px;
        `;
        
        const moveLabel = document.createElement('label');
        moveLabel.style.cssText = 'display: flex; align-items: flex-start; cursor: pointer;';
        
        const moveCheckbox = document.createElement('input');
        moveCheckbox.type = 'checkbox';
        moveCheckbox.id = 'move-shards-modal';
        moveCheckbox.style.cssText = `
            margin-right: 12px;
            margin-top: 2px;
            width: 18px;
            height: 18px;
            cursor: pointer;
        `;
        
        const moveTextContainer = document.createElement('div');
        const moveTitle = document.createElement('div');
        moveTitle.textContent = '⚠️ Move shards';
        moveTitle.style.cssText = 'font-weight: 600; color: #856404; margin-bottom: 4px;';
        
        const moveDescription = document.createElement('div');
        moveDescription.textContent = 'Remove shards from source node after sync';
        moveDescription.style.cssText = 'font-size: 13px; color: #856404;';
        
        moveTextContainer.appendChild(moveTitle);
        moveTextContainer.appendChild(moveDescription);
        
        moveLabel.appendChild(moveCheckbox);
        moveLabel.appendChild(moveTextContainer);
        moveSection.appendChild(moveLabel);
        contentArea.appendChild(moveSection);

        // Footer with buttons
        const footer = document.createElement('div');
        footer.style.cssText = `
            padding: 20px 24px;
            background: #f8f9fa;
            border-top: 1px solid #e9ecef;
            display: flex;
            gap: 12px;
            justify-content: flex-end;
        `;

        const cancelButton = document.createElement('button');
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
            
            if (!targetPeerId) {
                alert('Please select a target node');
                targetSelect.focus();
                return;
            }

            const operationType = isMoveShards ? 'move' : 'sync';
            const targetDisplayName = targetSelect.options[targetSelect.selectedIndex].textContent;
            
            if (!confirm(`Are you sure you want to ${operationType} shards [${selectedShards.join(', ')}] to ${targetDisplayName}?`)) {
                return;
            }

            modal.style.animation = 'fadeOut 0.2s ease-out';
            setTimeout(() => document.body.removeChild(modal), 200);

            try {
                const response = await fetch(this.replicateShardsEndpoint, {
                    method: 'POST',
                    headers: {
                        'Content-Type': 'application/json',
                    },
                    body: JSON.stringify({
                        sourcePeerId: sourceNodeInfo.peerId,
                        targetPeerId: targetPeerId,
                        collectionName: collection.name,
                        shardIdsToReplicate: selectedShards,
                        isMoveShards: isMoveShards
                    })
                });

                if (!response.ok) {
                    const error = await response.json();
                    throw new Error(error.details || `Failed to ${operationType} shards`);
                }

                this.showToast(`Shard ${operationType} initiated successfully`, 'success', 'Sync Started', 5000);
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

        // View Vigilante Logs button in header
        const viewVigilanteLogsBtn = document.getElementById('viewVigilanteLogs');
        if (viewVigilanteLogsBtn) {
            viewVigilanteLogsBtn.addEventListener('click', () => {
                this.openVigilanteLogs();
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
}

// Initialize dashboard when page loads and store it globally
let dashboard = null;
document.addEventListener('DOMContentLoaded', () => {
    console.log('DOM loaded, initializing dashboard');
    dashboard = new VigilanteDashboard();
    window.dashboard = dashboard; // Store for debugging
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
