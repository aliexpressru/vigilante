class VigilanteDashboard {
    constructor() {
        this.statusApiEndpoint = '/api/v1/cluster/status';
        this.sizesApiEndpoint = '/api/v1/cluster/collections-info';
        this.replicateShardsEndpoint = '/api/v1/cluster/replicate-shards';
        this.deleteCollectionEndpoint = '/api/v1/cluster/collection';
        this.refreshInterval = 0;
        this.intervalId = null;
        this.openCollections = new Set();
        this.selectedState = new Map();
        this.deletionStatus = new Map(); // Track deletion status per collection
        this.currentActiveNode = null; // Track currently active node
        this.init();
        this.setupRefreshControls();
    }

    // Convert numeric status to string
    getStatusText(statusCode) {
        const statusMap = {
            0: 'Healthy',
            1: 'Degraded', 
            2: 'Unavailable'
        };
        return statusMap[statusCode] || 'Unknown';
    }

    getStatusClass(statusCode) {
        const classMap = {
            0: 'healthy',
            1: 'degraded',
            2: 'unavailable'
        };
        return classMap[statusCode] || 'loading';
    }

    init() {
        // Load initial data but don't start auto-refresh by default
        this.loadClusterStatus();
        this.loadCollectionSizes();
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
            .filter(([otherPodName, info]) => info.peerId && info.peerId !== nodeInfo.peerId)
            .forEach(([otherPodName, info]) => {
                const button = document.createElement('button');
                button.type = 'button';
                button.className = 'peer-button';
                button.setAttribute('data-peer-id', info.peerId);
                button.setAttribute('title', `${otherPodName} (${info.peerId})`); // Add tooltip with full info
                button.textContent = otherPodName; // Show only pod name

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
        for (const [key, state] of this.selectedState.entries()) {
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
        
        // Update total size display
        const totalSizeElement = document.getElementById('totalCollectionsSize');
        if (totalSizeElement) {
            totalSizeElement.textContent = `Total Size: ${this.formatSize(totalSizeBytes)}`;
        }
        
        // Remember which collections were open before update
        const openCollections = new Set();
        document.querySelectorAll('.collection-details.visible').forEach(row => {
            const nameCell = row.previousElementSibling.querySelector('.collection-name-container span:first-child');
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

            acc[info.collectionName].nodes[info.podName] = {
                size: info.metrics?.size || 0,
                podName: info.podName,
                peerId: info.peerId || '',
                nodeUrl: info.nodeUrl || '',
                podNamespace: info.podNamespace || '',
                metrics: info.metrics || {}
            };
            return acc;
        }, {});

        const podNames = [...new Set(collections.map(info => info.podName).filter(Boolean))].sort();
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
                nameCell.colSpan = podNames.length + 1;
                
                // Create a container for name and size
                const nameContainer = document.createElement('div');
                nameContainer.className = 'collection-name-container';
                
                const nameSpan = document.createElement('span');
                nameSpan.textContent = collection.name;
                nameSpan.title = collection.name; // Show full name on hover
                nameContainer.appendChild(nameSpan);
                
                const sizeSpan = document.createElement('span');
                sizeSpan.className = 'collection-size';
                sizeSpan.textContent = this.formatSize(collectionTotalSize);
                nameContainer.appendChild(sizeSpan);
                
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
                
                deleteButtonsContainer.appendChild(deleteAllApiButton);
                deleteButtonsContainer.appendChild(deleteAllDiskButton);
                nameContainer.appendChild(deleteButtonsContainer);
                
                nameCell.appendChild(nameContainer);
                row.appendChild(nameCell);

                const detailsRow = document.createElement('tr');
                detailsRow.className = 'collection-details';
                if (openCollections.has(collection.name)) {
                    detailsRow.classList.add('visible');
                }

                const detailsCell = document.createElement('td');
                detailsCell.colSpan = podNames.length + 1;
                const detailsContent = document.createElement('div');
                detailsContent.className = 'collection-details-content';

                podNames.forEach(podName => {
                    const nodeInfo = collection.nodes[podName];
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
                        
                        // 1. Format size first (without label)
                        let sizeHtml = '';
                        if (nodeInfo.metrics.sizeBytes) {
                            const formattedSize = this.formatSize(nodeInfo.metrics.sizeBytes);
                            sizeHtml = `<div class="size-metric-standalone">${formattedSize}</div>`;
                        }
                        
                        // 2. Get shards HTML (includes Target nodes, Shards, and action controls)
                        const shardsHtml = nodeInfo.metrics.shards ? 
                            this.formatMetricValue('shards', nodeInfo.metrics.shards, nodeInfo) : '';
                        
                        // 3. Get transfers HTML
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
                            
                        nodeDetails.innerHTML = `
                            <div class="node-info-header">
                                <h4>${podName}${peerIdDisplay}</h4>
                                ${sizeHtml}
                            </div>
                            ${shardsHtml}
                            ${otherMetricsHtml ? `<dl class="other-metrics">${otherMetricsHtml}</dl>` : ''}
                            <div class="node-deletion-controls">
                                <button class="delete-api-button" data-collection="${collection.name}" data-node-url="${nodeInfo.nodeUrl || ''}" data-pod-name="${podName}" data-pod-namespace="${nodeInfo.podNamespace || ''}" title="Delete collection via API on this node">
                                    🗑️ API
                                </button>
                                <button class="delete-disk-button" data-collection="${collection.name}" data-node-url="${nodeInfo.nodeUrl || ''}" data-pod-name="${podName}" data-pod-namespace="${nodeInfo.podNamespace || ''}" title="Delete collection from disk on this node">
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

    updateUI(clusterState) {
        this.updateOverallStatus(clusterState);
        this.updateIssues(clusterState.health.issues);
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

    updateIssues(issues) {
        const issuesCard = document.getElementById('issuesCard');
        const issuesList = document.getElementById('issuesList');

        if (issues && issues.length > 0) {
            issuesCard.style.display = 'block';
            issuesList.innerHTML = '';
            
            issues.forEach(issue => {
                const li = document.createElement('li');
                li.textContent = issue;
                issuesList.appendChild(li);
            });
        } else {
            issuesCard.style.display = 'none';
        }
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

        console.log(`Creating cards for ${nodes.length} nodes`);
        nodes.forEach(node => {
            const nodeCard = this.createNodeCard(node);
            nodesGrid.appendChild(nodeCard);
        });

        console.log('Nodes UI updated, triggering collection sizes load');
        this.loadCollectionSizes();
    }

    createNodeCard(node) {
        const card = document.createElement('div');
        card.className = `node-card ${node.isHealthy ? 'healthy' : 'unhealthy'}`;

        const header = document.createElement('div');
        header.className = 'node-header';

        const nodeId = document.createElement('div');
        nodeId.className = 'node-id';
        nodeId.textContent = node.peerId;

        if (node.isLeader) {
            const leaderBadge = document.createElement('span');
            leaderBadge.className = 'leader-badge';
            leaderBadge.textContent = 'LEADER';
            nodeId.appendChild(leaderBadge);
        }

        const status = document.createElement('div');
        status.className = `node-status ${node.isHealthy ? 'healthy' : 'unhealthy'}`;
        status.textContent = node.isHealthy ? 'Healthy' : 'Unhealthy';

        header.appendChild(nodeId);
        header.appendChild(status);

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
        const scopeLabel = singleNode ? `on ${podName || nodeUrl}` : 'on all nodes';
        
        if (!confirm(`Are you sure you want to delete collection '${collectionName}' via ${typeLabel} ${scopeLabel}?\n\nThis action cannot be undone!`)) {
            return;
        }

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
                this.showDeletionResult(collectionName, result, true);
                // Refresh after a short delay to allow deletion to complete
                setTimeout(() => this.refresh(), 1000);
            } else {
                this.showDeletionResult(collectionName, result, false);
            }
        } catch (error) {
            alert(`Error deleting collection: ${error.message}`);
        }
    }

    showDeletionResult(collectionName, result, success) {
        const message = document.createElement('div');
        message.className = `deletion-message ${success ? 'success' : 'error'}`;
        
        let html = `<div class="deletion-message-header">
            <strong>${success ? '✓' : '✗'} ${result.message}</strong>
            <button class="close-button" onclick="this.parentElement.parentElement.remove()">×</button>
        </div>`;
        
        if (result.results && Object.keys(result.results).length > 0) {
            html += '<div class="deletion-results">';
            for (const [node, nodeResult] of Object.entries(result.results)) {
                const icon = nodeResult.success ? '✓' : '✗';
                const statusClass = nodeResult.success ? 'success' : 'error';
                html += `<div class="node-deletion-result ${statusClass}">
                    <span class="result-icon">${icon}</span>
                    <span class="result-node">${node}</span>
                    ${nodeResult.error ? `<span class="result-error">${nodeResult.error}</span>` : ''}
                </div>`;
            }
            html += '</div>';
        }
        
        message.innerHTML = html;
        
        const header = document.querySelector('.header');
        header.insertAdjacentElement('afterend', message);
        
        // Auto-remove after 10 seconds
        setTimeout(() => {
            if (message.parentNode) {
                message.remove();
            }
        }, 10000);
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
