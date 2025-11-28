class VigilanteDashboard {
    constructor() {
        this.statusApiEndpoint = '/api/v1/cluster/status';
        this.sizesApiEndpoint = '/api/v1/collections/info';
        this.snapshotsApiEndpoint = '/api/v1/snapshots/info';
        this.replicateShardsEndpoint = '/api/v1/cluster/replicate-shards';
        this.deleteCollectionEndpoint = '/api/v1/collections';
        this.createSnapshotEndpoint = '/api/v1/snapshots';
        this.deleteSnapshotEndpoint = '/api/v1/snapshots';
        this.downloadSnapshotEndpoint = '/api/v1/snapshots/download';
        this.recoverFromSnapshotEndpoint = '/api/v1/snapshots/recover';
        this.deletePodEndpoint = '/api/v1/kubernetes/delete-pod';
        this.manageStatefulSetEndpoint = '/api/v1/kubernetes/manage-statefulset';
        this.refreshInterval = 0;
        this.intervalId = null;
        this.openSnapshots = new Set();
        this.selectedState = new Map();
        this.toastIdCounter = 0; // Counter for unique toast IDs
        this.clusterIssues = []; // Issues from cluster/status
        this.collectionIssues = []; // Issues from collections-info
        this.clusterNodes = []; // Store cluster nodes for StatefulSet management
        this.init();
        this.setupRefreshControls();
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
    }

    setupRefreshControls() {
        const intervalSelect = document.getElementById('refreshInterval');
        const manualRefreshBtn = document.getElementById('manualRefresh');

        // Set initial value
        intervalSelect.value = this.refreshInterval.toString();

        // Handle interval changes
        intervalSelect.addEventListener('change', (e) => {
            const newInterval = parseInt(e.target.value);
            this.refreshInterval = newInterval;
            
            this.stopAutoRefresh();
            if (newInterval > 0) {
                this.skipNextRefresh = true; // Skip the immediate refresh
                this.startAutoRefresh();
            }
        });

        // Handle manual refresh
        manualRefreshBtn.addEventListener('click', () => {
            this.refresh();
        });
    }

    refresh() {
        this.loadClusterStatus();
        this.loadCollectionSizes();
        this.loadSnapshots();
    }

    startAutoRefresh() {
        if (this.intervalId) {
            clearInterval(this.intervalId);
        }
        if (this.refreshInterval > 0) {
            this.intervalId = setInterval(() => this.refresh(), this.refreshInterval);
        }
    }

    stopAutoRefresh() {
        if (this.intervalId) {
            clearInterval(this.intervalId);
            this.intervalId = null;
        }
    }

    // Toast notification methods
    showToast(message, type = 'info', title = null, duration = 5000, isLoading = false) {
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
        try {
            this.showRefreshAnimation();
            const response = await fetch(this.statusApiEndpoint);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            this.updateUI(data);
            
        } catch (error) {
            console.error('Error fetching cluster status:', error);
            this.showError(error.message);
        } finally {
            this.hideRefreshAnimation();
        }
    }

    async loadCollectionSizes() {
        try {
            const response = await fetch(this.sizesApiEndpoint);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            // Handle both direct array and nested collections property
            const collections = Array.isArray(data) ? data : (data.collections || []);
            
            // Extract collection issues if present
            this.collectionIssues = data.issues || [];
            
            // Update combined issues display
            this.updateCombinedIssues();
            
            this.updateCollectionSizes(collections);
            
        } catch (error) {
            console.error('Error fetching collection sizes:', error);
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
                    <div class="target-nodes-section">
                        <div class="target-nodes-label">Target nodes</div>
                        <div class="peer-buttons-container">
                            <!-- Peer buttons will be added dynamically -->
                        </div>
                    </div>
                    <div class="shards-section">
                        <div class="shards-label">Shards</div>
                        <div class="shards-grid">
                            ${shardsHtml}
                        </div>
                    </div>
                    <div class="action-controls">
                        <label class="move-shards-label">
                            <input type="checkbox" class="move-shards-checkbox">
                            Move
                        </label>
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

    setupPeerButtons(nodeDetails, collection, nodeInfo, savedState) {
        const peerButtonsContainer = nodeDetails.querySelector('.peer-buttons-container');
        if (!peerButtonsContainer) return;

        const stateKey = nodeDetails.getAttribute('data-state-key');
        peerButtonsContainer.innerHTML = '';

        Object.entries(collection.nodes)
            .filter(([_, info]) => info.peerId && info.peerId !== nodeInfo.peerId)
            .forEach(([_, info]) => {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'peer-button';
                button.setAttribute('data-peer-id', info.peerId);
                
                // Show podName if available and not 'unknown', otherwise show peerId
                const displayName = info.podName && info.podName !== 'unknown' ? info.podName : info.peerId;
                button.setAttribute('title', `${info.podName} (${info.peerId})`);
                button.textContent = displayName;

                // Restore selected button state
                if (savedState.targetPeer === info.peerId) {
                    button.classList.add('selected');
                }

                button.addEventListener('click', () => {
                    this.clearOtherNodesState(stateKey);

                    // Remove selection from all buttons
                    peerButtonsContainer.querySelectorAll('.peer-button').forEach(btn => {
                        btn.classList.remove('selected');
                    });

                    // Highlight the selected button
                    button.classList.add('selected');

                    // Save the selected peer
                    const currentState = this.selectedState.get(stateKey) || {
                        selectedShards: new Set(),
                        targetPeer: '',
                        moveChecked: false
                    };

                    currentState.targetPeer = info.peerId;
                    this.selectedState.set(stateKey, currentState);
                });

                peerButtonsContainer.appendChild(button);
            });
    }

    clearOtherNodesState(currentStateKey) {
        // Clear selection on all nodes except the current one
        for (const [key, _] of this.selectedState.entries()) {
            if (key !== currentStateKey) {
                const nodeDetails = document.querySelector(`[data-state-key="${key}"]`);
                if (nodeDetails) {
                    // Clear peer buttons selection
                    nodeDetails.querySelectorAll('.peer-button').forEach(btn => {
                        btn.classList.remove('selected');
                    });

                    // Uncheck shard checkboxes
                    nodeDetails.querySelectorAll('.shard-checkbox').forEach(checkbox => {
                        checkbox.checked = false;
                    });

                    // Uncheck move checkbox
                    const moveCheckbox = nodeDetails.querySelector('.move-shards-checkbox');
                    if (moveCheckbox) {
                        moveCheckbox.checked = false;
                    }
                }
                this.selectedState.delete(key);
            }
        }
    }

    updateCollectionSizes(collections) {
        if (!Array.isArray(collections)) {
            console.warn('Received non-array collections data:', collections);
            collections = [];
        }
        
        // Calculate total size
        let totalSizeBytes = 0;
        collections.forEach(info => {
            if (info?.metrics?.sizeBytes) {
                totalSizeBytes += info.metrics.sizeBytes;
            }
        });
        
        // Group collections by name to count unique collections
        const uniqueCollections = new Set(collections.map(info => info.collectionName).filter(Boolean));
        const collectionsCount = uniqueCollections.size;
        
        // Update collections count display
        const totalCountElement = document.getElementById('totalCollectionsCount');
        if (totalCountElement) {
            totalCountElement.textContent = `Collections: ${collectionsCount}`;
        }
        
        // Update total size display
        const totalSizeElement = document.getElementById('totalCollectionsSize');
        if (totalSizeElement) {
            totalSizeElement.textContent = `Total Size: ${this.formatSize(totalSizeBytes)}`;
        }
        
        // Remember which collections were open before update
        const openCollections = new Set();
        document.querySelectorAll('.collection-details.visible').forEach(row => {
            const nameCell = row.previousElementSibling.querySelector('.collection-name-line');
            if (nameCell) {
                openCollections.add(nameCell.textContent);
            }
        });
        
        // Group collections by name and sort them
        const collectionsByName = collections.reduce((acc, info) => {
            if (!info || !info.collectionName) {
                console.warn('Invalid collection info:', info);
                return acc;
            }

            if (!acc[info.collectionName]) {
                acc[info.collectionName] = {
                    name: info.collectionName,
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
                Object.values(collection.nodes).forEach(nodeInfo => {
                    if (nodeInfo.metrics?.sizeBytes) {
                        collectionTotalSize += nodeInfo.metrics.sizeBytes;
                    }
                });
                
                const nameCell = document.createElement('td');
                nameCell.className = 'collection-name';
                nameCell.colSpan = nodeKeys.length + 1;
                
                // Create a container for the entire collection header
                const headerContainer = document.createElement('div');
                headerContainer.className = 'collection-header-container';
                
                // First line: collection name only
                const nameDiv = document.createElement('div');
                nameDiv.className = 'collection-name-line';
                nameDiv.textContent = collection.name;
                nameDiv.title = collection.name;
                headerContainer.appendChild(nameDiv);
                
                // Second line: delete buttons and size
                const actionsDiv = document.createElement('div');
                actionsDiv.className = 'collection-actions-line';
                
                // Add collection-wide delete buttons
                const deleteButtonsContainer = document.createElement('div');
                deleteButtonsContainer.className = 'collection-delete-buttons';
                
                const deleteAllApiButton = document.createElement('button');
                deleteAllApiButton.className = 'delete-all-api-button';
                deleteAllApiButton.innerHTML = '🗑️ API';
                deleteAllApiButton.title = 'Delete collection via API on all nodes';
                deleteAllApiButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.deleteCollection(collection.name, 'Api', false);
                };
                
                const deleteAllDiskButton = document.createElement('button');
                deleteAllDiskButton.className = 'delete-all-disk-button';
                deleteAllDiskButton.innerHTML = '🗑️ Disk';
                deleteAllDiskButton.title = 'Delete collection from disk on all nodes';
                deleteAllDiskButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.deleteCollection(collection.name, 'Disk', false);
                };
                
                const createSnapshotAllButton = document.createElement('button');
                createSnapshotAllButton.className = 'create-snapshot-all-button';
                createSnapshotAllButton.innerHTML = '📸 Snapshot All';
                createSnapshotAllButton.title = 'Create snapshot on all nodes';
                createSnapshotAllButton.onclick = async (e) => {
                    e.stopPropagation();
                    await this.createSnapshot(collection.name, null, true);
                };
                
                deleteButtonsContainer.appendChild(deleteAllApiButton);
                deleteButtonsContainer.appendChild(deleteAllDiskButton);
                deleteButtonsContainer.appendChild(createSnapshotAllButton);
                actionsDiv.appendChild(deleteButtonsContainer);
                
                const sizeSpan = document.createElement('span');
                sizeSpan.className = 'collection-size';
                sizeSpan.textContent = this.formatSize(collectionTotalSize);
                actionsDiv.appendChild(sizeSpan);
                
                headerContainer.appendChild(actionsDiv);
                nameCell.appendChild(headerContainer);
                row.appendChild(nameCell);

                const detailsRow = document.createElement('tr');
                detailsRow.className = 'collection-details';
                if (openCollections.has(collection.name)) {
                    detailsRow.classList.add('visible');
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
                        const savedState = this.selectedState.get(stateKey) || {
                            selectedShards: new Set(),
                            targetPeer: '',
                            moveChecked: false
                        };
                        
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
                                              key !== 'shard_states' && key !== 'shards' && key !== 'outgoingTransfers' && key !== 'snapshots')
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
                        
                        // Create Snapshot button only
                        const createSnapshotHtml = `
                            <div class="create-snapshot-section">
                                <button class="create-snapshot-btn" title="Create new snapshot for this collection">
                                    <i class="fas fa-camera"></i> Create Snapshot
                                </button>
                            </div>
                        `;
                        
                        nodeDetails.innerHTML = `
                            <div class="node-info-header">
                                <h4 title="${fullNodeTitle}">${displayName}${peerIdDisplay}</h4>
                            </div>
                            <div class="node-size-line">
                                ${sizeForHeader}
                            </div>
                            ${shardsHtml}
                            ${otherMetricsHtml ? `<dl class="other-metrics">${otherMetricsHtml}</dl>` : ''}
                            ${createSnapshotHtml}
                            <div class="node-deletion-controls">
                                <button class="delete-api-button" data-collection="${collection.name}" data-node-url="${nodeInfo.nodeUrl || ''}" data-pod-name="${nodeInfo.podName || ''}" data-pod-namespace="${nodeInfo.podNamespace || ''}" title="Delete collection via API on this node">
                                    🗑️ API
                                </button>
                                <button class="delete-disk-button" data-collection="${collection.name}" data-node-url="${nodeInfo.nodeUrl || ''}" data-pod-name="${nodeInfo.podName || ''}" data-pod-namespace="${nodeInfo.podNamespace || ''}" title="Delete collection from disk on this node">
                                    🗑️ Disk
                                </button>
                            </div>
                            ${transfersHtml ? `<dl class="transfers-metrics">${transfersHtml}</dl>` : ''}
                        `;

                        nodeDetails.setAttribute('data-state-key', stateKey);
                        
                        // Set up peer buttons after the HTML is added
                        this.setupPeerButtons(nodeDetails, collection, nodeInfo, savedState);

                        // Restore move checkbox state
                        const moveCheckbox = nodeDetails.querySelector('.move-shards-checkbox');
                        if (moveCheckbox) {
                            moveCheckbox.checked = savedState.moveChecked;
                            moveCheckbox.addEventListener('change', () => {
                                const currentState = this.selectedState.get(stateKey) || {
                                    selectedShards: new Set(),
                                    targetPeer: savedState.targetPeer,
                                    moveChecked: false
                                };
                                this.selectedState.set(stateKey, {
                                    ...currentState,
                                    moveChecked: moveCheckbox.checked
                                });
                            });
                        }

                        // Restore selected shards
                        const shardCheckboxes = nodeDetails.querySelectorAll('.shard-checkbox');
                        shardCheckboxes.forEach(checkbox => {
                            const shardId = parseInt(checkbox.dataset.shardId);
                            checkbox.checked = savedState.selectedShards.has(shardId);
                            checkbox.addEventListener('change', () => {
                                const stateKey = nodeDetails.getAttribute('data-state-key');
                                this.clearOtherNodesState(stateKey);

                                const currentState = this.selectedState.get(stateKey) || {
                                    selectedShards: new Set(),
                                    targetPeer: savedState.targetPeer,
                                    moveChecked: savedState.moveChecked
                                };
                                
                                if (checkbox.checked) {
                                    currentState.selectedShards.add(shardId);
                                } else {
                                    currentState.selectedShards.delete(shardId);
                                }
                                
                                this.selectedState.set(stateKey, currentState);
                            });
                        });

                        // Setup replicate button
                        const replicateButton = nodeDetails.querySelector('.replicate-button');
                        if (replicateButton) {
                            replicateButton.addEventListener('click', async () => {
                                const selectedShards = Array.from(
                                    nodeDetails.querySelectorAll('.shard-checkbox:checked')
                                ).map(cb => parseInt(cb.dataset.shardId));

                                const selectedPeerButton = nodeDetails.querySelector('.peer-button.selected');
                                const targetPeerId = selectedPeerButton?.getAttribute('data-peer-id');
                                const isMoveShards = nodeDetails.querySelector('.move-shards-checkbox').checked;
                                
                                if (!targetPeerId) {
                                    alert('Please select a target peer');
                                    return;
                                }
                                
                                if (selectedShards.length === 0) {
                                    alert('Please select at least one shard to sync');
                                    return;
                                }

                                const operationType = isMoveShards ? 'move' : 'sync';
                                if (!confirm(`Are you sure you want to ${operationType} the selected shards to peer ${targetPeerId}?`)) {
                                    return;
                                }

                                try {
                                    const response = await fetch(this.replicateShardsEndpoint, {
                                        method: 'POST',
                                        headers: {
                                            'Content-Type': 'application/json',
                                        },
                                        body: JSON.stringify({
                                            sourcePeerId: nodeInfo.peerId,
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

                                    this.selectedState.delete(stateKey);
                                    alert(`Shard ${operationType} initiated successfully`);
                                    this.refresh();
                                } catch (error) {
                                    alert(`Error: ${error.message}`);
                                }
                            });
                        }

                        // Setup delete buttons
                        const deleteApiButton = nodeDetails.querySelector('.delete-api-button');
                        if (deleteApiButton) {
                            deleteApiButton.addEventListener('click', async (e) => {
                                e.stopPropagation();
                                const collectionName = deleteApiButton.dataset.collection;
                                const nodeUrl = deleteApiButton.dataset.nodeUrl;
                                await this.deleteCollection(collectionName, 'Api', true, nodeUrl, null, null);
                            });
                        }

                        const deleteDiskButton = nodeDetails.querySelector('.delete-disk-button');
                        if (deleteDiskButton) {
                            deleteDiskButton.addEventListener('click', async (e) => {
                                e.stopPropagation();
                                const collectionName = deleteDiskButton.dataset.collection;
                                const podName = deleteDiskButton.dataset.podName;
                                const podNamespace = deleteDiskButton.dataset.podNamespace;
                                await this.deleteCollection(collectionName, 'Disk', true, null, podName, podNamespace);
                            });
                        }

                        // Setup create snapshot button
                        const createSnapshotBtn = nodeDetails.querySelector('.create-snapshot-btn');
                        if (createSnapshotBtn) {
                            createSnapshotBtn.addEventListener('click', async (e) => {
                                e.stopPropagation();
                                await this.createSnapshot(collection.name, nodeInfo.nodeUrl, false, nodeInfo.podName);
                            });
                        }


                        detailsContent.appendChild(nodeDetails);
                    }
                });

                detailsCell.appendChild(detailsContent);
                detailsRow.appendChild(detailsCell);

                row.addEventListener('click', () => {
                    const wasVisible = detailsRow.classList.contains('visible');
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

    async loadSnapshots() {
        try {
            const response = await fetch(this.snapshotsApiEndpoint);
            
            if (!response.ok) {
                throw new Error(`HTTP error! status: ${response.status}`);
            }
            
            const data = await response.json();
            const snapshots = Array.isArray(data) ? data : (data.snapshots || []);
            this.updateSnapshotsTable(snapshots);
            
        } catch (error) {
            console.error('Error fetching snapshots:', error);
        }
    }

    updateSnapshotsTable(snapshots) {
        if (!snapshots || snapshots.length === 0) {
            const container = document.getElementById('snapshotsTable');
            container.innerHTML = '<p style="color: #999; padding: 20px; text-align: center;">No snapshots found</p>';
            document.getElementById('totalSnapshotsSize').textContent = 'Total: 0 B';
            return;
        }

        // Group by collection name only (each node can have unique snapshot name)
        const grouped = {};
        let totalSize = 0;

        snapshots.forEach(snapshot => {
            const key = snapshot.collectionName;
            if (!grouped[key]) {
                grouped[key] = {
                    collectionName: snapshot.collectionName,
                    totalSize: 0,
                    nodes: {}
                };
            }
            grouped[key].totalSize += snapshot.sizeBytes;
            totalSize += snapshot.sizeBytes;
            
            // Store snapshot info per node URL
            grouped[key].nodes[snapshot.nodeUrl] = snapshot;
        });

        // Update total size display
        document.getElementById('totalSnapshotsSize').textContent = `Total: ${this.formatSize(totalSize)}`;

        // Create table
        const table = document.createElement('table');
        const tbody = document.createElement('tbody');

        Object.values(grouped).forEach(collection => {
            // Main collection row
            const row = document.createElement('tr');
            row.className = 'snapshot-row';
            
            const key = collection.collectionName;
            const isOpen = this.openSnapshots.has(key);

            const td = document.createElement('td');
            td.colSpan = 1;
            
            const container = document.createElement('div');
            container.className = 'snapshot-header-container';
            
            const nameLine = document.createElement('div');
            nameLine.className = 'snapshot-name-line';
            nameLine.innerHTML = `
                <i class="fas fa-camera" style="color: #7b1fa2; margin-right: 8px;"></i>
                <strong>${collection.collectionName}</strong>
            `;
            
            const actionsLine = document.createElement('div');
            actionsLine.className = 'snapshot-actions-line';
            
            const buttonsGroup = document.createElement('div');
            buttonsGroup.className = 'snapshot-buttons-group';
            
            const recoverAllBtn = document.createElement('button');
            recoverAllBtn.className = 'action-button action-button-success';
            recoverAllBtn.innerHTML = '<i class="fas fa-undo"></i> Recover All';
            recoverAllBtn.onclick = (e) => {
                e.stopPropagation();
                this.recoverSnapshotOnAllNodes(collection);
            };
            
            const deleteAllBtn = document.createElement('button');
            deleteAllBtn.className = 'action-button action-button-danger';
            deleteAllBtn.innerHTML = '<i class="fas fa-trash"></i> Delete All';
            deleteAllBtn.onclick = (e) => {
                e.stopPropagation();
                this.deleteSnapshotFromAllNodes(collection);
            };
            
            buttonsGroup.appendChild(recoverAllBtn);
            buttonsGroup.appendChild(deleteAllBtn);
            actionsLine.appendChild(buttonsGroup);
            
            const sizeSpan = document.createElement('span');
            sizeSpan.className = 'snapshot-size';
            sizeSpan.textContent = this.formatSize(collection.totalSize);
            actionsLine.appendChild(sizeSpan);
            
            container.appendChild(nameLine);
            container.appendChild(actionsLine);
            td.appendChild(container);
            row.appendChild(td);

            // Details row for nodes
            const detailsRow = document.createElement('tr');
            detailsRow.className = `snapshot-details ${isOpen ? 'visible' : ''}`;
            const detailsTd = document.createElement('td');
            detailsTd.colSpan = 1;
            
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

            Object.values(collection.nodes).forEach(node => {
                const nodeRow = document.createElement('tr');
                // Escape quotes in snapshot name for onclick attributes
                // Create cells
                const cellNode = document.createElement('td');
                cellNode.textContent = node.nodeUrl;
                
                const cellPeer = document.createElement('td');
                cellPeer.innerHTML = `<code>${node.peerId}</code>`;
                
                const cellPod = document.createElement('td');
                cellPod.textContent = node.podName;
                
                const cellSnapshot = document.createElement('td');
                cellSnapshot.textContent = node.snapshotName;
                
                const cellSize = document.createElement('td');
                cellSize.textContent = node.prettySize;
                
                const cellActions = document.createElement('td');
                const actionsContainer = document.createElement('div');
                actionsContainer.className = 'snapshot-actions-cell';
                
                const downloadBtn = document.createElement('button');
                downloadBtn.className = 'action-button action-button-primary action-button-sm';
                downloadBtn.innerHTML = '<i class="fas fa-download"></i>';
                downloadBtn.title = 'Download snapshot (tries API first, then disk fallback)';
                downloadBtn.onclick = () => this.downloadSnapshot(
                    collection.collectionName, 
                    node.snapshotName, 
                    node.nodeUrl,
                    node.podName, 
                    node.podNamespace || 'qdrant'
                );
                
                const recoverBtn = document.createElement('button');
                recoverBtn.className = 'action-button action-button-success action-button-sm';
                recoverBtn.innerHTML = '<i class="fas fa-undo"></i>';
                recoverBtn.title = 'Recover from this snapshot';
                recoverBtn.onclick = () => this.recoverSnapshotFromNode(node.nodeUrl, collection.collectionName, node.snapshotName, node.podName);
                
                const deleteBtn = document.createElement('button');
                deleteBtn.className = 'action-button action-button-danger action-button-sm';
                deleteBtn.innerHTML = '<i class="fas fa-trash"></i>';
                deleteBtn.title = 'Delete this snapshot';
                deleteBtn.onclick = () => this.deleteSnapshotFromNode(node);
                
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

            detailsTd.appendChild(nodesTable);
            detailsRow.appendChild(detailsTd);

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

    async recoverSnapshotFromNode(nodeUrl, collectionName, snapshotName, podName = null) {
        // Show podName if available and not 'unknown', otherwise show nodeUrl
        const nodeIdentifier = podName && podName !== 'unknown' ? podName : nodeUrl;
        const toastId = this.showToast(`Recovering ${collectionName} from ${snapshotName} on ${nodeIdentifier}...`, 'info', 0);
        
        try {
            const response = await fetch(this.recoverFromSnapshotEndpoint, {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    nodeUrl,
                    collectionName,
                    snapshotName
                })
            });

            const result = await response.json();
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', 5000);
            } else {
                this.showToast(`✗ ${result.message || 'Recovery failed'}`, 'error', 5000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error recovering snapshot: ${error.message}`, 'error', 5000);
        }
    }

    async recoverSnapshotOnAllNodes(collection) {
        const nodes = Object.values(collection.nodes);
        const toastId = this.showToast(`Recovering ${collection.collectionName} on ${nodes.length} nodes...`, 'info', 0);
        
        try {
            const promises = nodes.map(node => 
                fetch(this.recoverFromSnapshotEndpoint, {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        nodeUrl: node.nodeUrl,
                        collectionName: collection.collectionName,
                        snapshotName: node.snapshotName // Each node has its own snapshot name
                    })
                })
            );

            const results = await Promise.all(promises);
            this.removeToast(toastId);

            const successCount = results.filter(r => r.ok).length;
            if (successCount === results.length) {
                this.showToast(`✓ Successfully recovered ${collection.collectionName} on all ${nodes.length} nodes`, 'success', 5000);
            } else {
                this.showToast(`⚠ Recovered on ${successCount}/${nodes.length} nodes`, 'warning', 5000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error recovering snapshots: ${error.message}`, 'error', 5000);
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
            const requestBody = {
                collectionName: snapshot.collectionName,
                snapshotName: snapshot.snapshotName,
                singleNode: true,
                source: snapshot.source,
                nodeUrl: snapshot.nodeUrl,
                podName: snapshot.podName,
                podNamespace: snapshot.podNamespace
            };

            const response = await fetch(this.deleteSnapshotEndpoint, {
                method: 'DELETE',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', 5000);
                this.loadSnapshots(); // Reload to update UI
            } else {
                this.showToast(`✗ ${result.message || 'Deletion failed'}`, 'error', 5000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error deleting snapshot: ${error.message}`, 'error', 5000);
        }
    }

    async deleteSnapshotFromAllNodes(collection) {
        const nodes = Object.values(collection.nodes);
        if (!confirm(`Delete all snapshots for ${collection.collectionName} from all ${nodes.length} nodes?`)) {
            return;
        }

        const toastId = this.showToast(`Deleting snapshots for ${collection.collectionName} from ${nodes.length} nodes...`, 'info', 0);
        
        try {
            const promises = nodes.map(node => 
                fetch(this.deleteSnapshotEndpoint, {
                    method: 'DELETE',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({
                        collectionName: collection.collectionName,
                        snapshotName: node.snapshotName,
                        singleNode: true,
                        source: node.source,
                        nodeUrl: node.nodeUrl,
                        podName: node.podName,
                        podNamespace: node.podNamespace
                    })
                })
            );

            const results = await Promise.all(promises);
            this.removeToast(toastId);

            const successCount = results.filter(r => r.ok).length;
            if (successCount === results.length) {
                this.showToast(`✓ Successfully deleted snapshots for ${collection.collectionName} from all ${nodes.length} nodes`, 'success', 5000);
                this.loadSnapshots(); // Reload to update UI
            } else {
                this.showToast(`⚠ Deleted from ${successCount}/${nodes.length} nodes`, 'warning', 5000);
                this.loadSnapshots(); // Reload to update UI
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error deleting snapshots: ${error.message}`, 'error', 5000);
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
                    <label for="recoverNodeUrl">Node URL:</label>
                    <input type="text" id="recoverNodeUrl" value="${nodeUrl}" readonly class="form-input" />
                </div>
                <div class="form-group">
                    <label for="recoverCollectionName">Collection Name:</label>
                    <input type="text" id="recoverCollectionName" placeholder="Enter collection name" class="form-input" required />
                </div>
                <div class="form-group">
                    <label for="recoverSnapshotUrl">Snapshot URL:</label>
                    <input type="text" id="recoverSnapshotUrl" placeholder="Enter snapshot URL (e.g., s3://...)" class="form-input" required />
                </div>
                <div class="form-group">
                    <label for="recoverSnapshotChecksum">Checksum (optional):</label>
                    <input type="text" id="recoverSnapshotChecksum" placeholder="Enter snapshot checksum" class="form-input" />
                </div>
                <div class="form-group">
                    <label class="checkbox-label">
                        <input type="checkbox" id="recoverWaitForResult" checked />
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
        setTimeout(() => document.getElementById('recoverCollectionName').focus(), 100);
        
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
        overlay.querySelector('.modal-submit').addEventListener('click', async () => {
            const collectionName = document.getElementById('recoverCollectionName').value.trim();
            const snapshotUrl = document.getElementById('recoverSnapshotUrl').value.trim();
            const snapshotChecksum = document.getElementById('recoverSnapshotChecksum').value.trim() || null;
            const waitForResult = document.getElementById('recoverWaitForResult').checked;
            
            if (!collectionName || !snapshotUrl) {
                this.showToast('Collection name and snapshot URL are required', 'error', 5000);
                return;
            }
            
            closeModal();
            await this.recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, waitForResult);
        });
        
        // Enter key handler for form inputs
        ['recoverCollectionName', 'recoverSnapshotUrl', 'recoverSnapshotChecksum'].forEach(id => {
            document.getElementById(id).addEventListener('keypress', (e) => {
                if (e.key === 'Enter') {
                    overlay.querySelector('.modal-submit').click();
                }
            });
        });
    }

    async recoverCollectionFromUrl(nodeUrl, collectionName, snapshotUrl, snapshotChecksum, waitForResult) {
        const toastId = this.showToast(
            `Recovering collection '${collectionName}' from URL on node ${nodeUrl}...`, 
            'info', 
            0
        );
        
        try {
            const response = await fetch('/api/v1/snapshots/recover-from-url', {
                method: 'POST',
                headers: { 'Content-Type': 'application/json' },
                body: JSON.stringify({
                    nodeUrl,
                    collectionName,
                    snapshotUrl,
                    snapshotChecksum,
                    waitForResult
                })
            });

            const result = await response.json();
            this.removeToast(toastId);

            if (response.ok && result.success) {
                this.showToast(`✓ ${result.message}`, 'success', 5000);
                // Reload data to reflect changes
                this.refresh();
            } else {
                this.showToast(`✗ ${result.message || 'Recovery failed'}`, 'error', 8000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`✗ Error recovering collection: ${error.message}`, 'error', 8000);
        }
    }

    updateUI(clusterState) {
        this.updateOverallStatus(clusterState);
        // Store cluster issues
        this.clusterIssues = clusterState.health.issues || [];
        // Update combined issues display
        this.updateCombinedIssues();
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

        // Add cluster issues section
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

    updateIssues(issues) {
        // Legacy method - kept for compatibility
        // Now handled by updateCombinedIssues
        console.warn('updateIssues is deprecated, use updateCombinedIssues instead');
    }

    updateNodes(nodes) {
        console.log('Updating nodes UI with:', nodes);
        const nodesGrid = document.getElementById('nodesGrid');
        nodesGrid.innerHTML = '';

        if (!nodes || nodes.length === 0) {
            console.log('No nodes available to display');
            nodesGrid.innerHTML = '<p>No nodes available</p>';
            return;
        }

        // Store nodes for StatefulSet management
        this.clusterNodes = nodes;

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

        // Error message (if any) - show short error in node card
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
                collectionName: collectionName,
                deletionType: deletionType,
                singleNode: singleNode
            };

            if (singleNode) {
                if (deletionType === 'Api') {
                    requestBody.nodeUrl = nodeUrl;
                } else {
                    requestBody.podName = podName;
                    requestBody.podNamespace = podNamespace;
                }
            }

            const response = await fetch(this.deleteCollectionEndpoint, {
                method: 'DELETE',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();
            
            if (response.ok && result.success) {
                this.showDeletionResultToast(toastId, collectionName, result, true);
                // Refresh after a short delay to allow deletion to complete
                setTimeout(() => this.refresh(), 1000);
            } else {
                this.showDeletionResultToast(toastId, collectionName, result, false);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error deleting collection: ${error.message}`, 'error', 'Deletion failed', 5000);
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

    showError(message) {
        // Remove any existing error messages
        const existingError = document.querySelector('.error-message');
        if (existingError) {
            existingError.remove();
        }

        // Create new error message
        const errorDiv = document.createElement('div');
        errorDiv.className = 'error-message';
        errorDiv.innerHTML = `
            <strong>Error loading cluster status:</strong> ${message}
            <br>
            <small>Retrying in ${this.refreshInterval / 1000} seconds...</small>
        `;

        // Insert after header
        const header = document.querySelector('.header');
        header.insertAdjacentElement('afterend', errorDiv);

        // Auto-remove after 4 seconds
        setTimeout(() => {
            if (errorDiv.parentNode) {
                errorDiv.remove();
            }
        }, 4000);
    }

    // Snapshot management methods
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
                collectionName: collectionName,
                singleNode: !onAllNodes,
                nodeUrl: onAllNodes ? null : nodeUrl
            };

            const response = await fetch(this.createSnapshotEndpoint, {
                method: 'POST',
                headers: {
                    'Content-Type': 'application/json',
                },
                body: JSON.stringify(requestBody)
            });

            const result = await response.json();

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

    async downloadSnapshot(collectionName, snapshotName, nodeUrl, podName, podNamespace) {
        const toastId = this.showToast(
            `Preparing download of '${snapshotName}'...`,
            'info',
            'Downloading',
            0,
            true
        );

        try {
            const requestBody = {
                collectionName: collectionName,
                snapshotName: snapshotName,
                nodeUrl: nodeUrl,
                podName: podName,
                podNamespace: podNamespace
            };

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
                this.showToast(result.error || result.details || 'Failed to delete pod', 'error', 'Deletion Failed', 5000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error deleting pod: ${error.message}`, 'error', 'Error', 5000);
        }
    }

    showStatefulSetDialog() {
        // Get StatefulSet name and namespace from first node
        const firstNode = this.clusterNodes && this.clusterNodes.length > 0 ? this.clusterNodes[0] : null;
        const statefulSetName = firstNode?.statefulSetName || 'qdrant';
        const namespace = firstNode?.namespace || 'qdrant';
        const currentReplicas = this.clusterNodes?.length || 3;

        console.log('Opening StatefulSet dialog with:', {
            statefulSetName,
            namespace,
            currentReplicas,
            firstNode
        });

        // Create modal overlay
        const overlay = document.createElement('div');
        overlay.className = 'modal-overlay';
        
        // Create modal dialog
        const modal = document.createElement('div');
        modal.className = 'modal-dialog statefulset-modal';
        modal.innerHTML = `
            <div class="modal-header">
                <h3><i class="fas fa-cubes"></i> Manage StatefulSet</h3>
                <button class="modal-close">&times;</button>
            </div>
            <div class="modal-body">
                <div class="statefulset-info">
                    <div class="info-item">
                        <span class="info-label">StatefulSet:</span>
                        <span class="info-value">${statefulSetName}</span>
                    </div>
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
                this.showToast(result.error || result.details || 'Failed to manage StatefulSet', 'error', 'Operation Failed', 5000);
            }
        } catch (error) {
            this.removeToast(toastId);
            this.showToast(`Error managing StatefulSet: ${error.message}`, 'error', 'Error', 5000);
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
