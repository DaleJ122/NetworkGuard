// Sidebar toggle for mobile
window.sidebarToggle = {
    toggle: function () {
        var sidebar = document.querySelector('.sidebar');
        var overlay = document.querySelector('.sidebar-overlay');
        if (sidebar.classList.contains('mobile-open')) {
            sidebar.classList.remove('mobile-open');
            overlay.classList.remove('show');
        } else {
            sidebar.classList.add('mobile-open');
            overlay.classList.add('show');
        }
    },
    close: function () {
        var sidebar = document.querySelector('.sidebar');
        var overlay = document.querySelector('.sidebar-overlay');
        if (sidebar) sidebar.classList.remove('mobile-open');
        if (overlay) overlay.classList.remove('show');
    }
};

window.chartInterop = {
    charts: {},
    metadata: {},

    createOrUpdate: function (canvasId, config, topDomains) {
        const canvas = document.getElementById(canvasId);
        if (!canvas) return;

        if (this.charts[canvasId]) {
            this.charts[canvasId].destroy();
        }

        // Store top domains lookup for tooltip
        if (topDomains) {
            this.metadata[canvasId] = topDomains;
        }

        // Set up custom tooltip if we have metadata
        if (topDomains && config.options && config.options.plugins) {
            const meta = this.metadata[canvasId];
            config.options.plugins.tooltip = {
                mode: 'nearest',
                intersect: true,
                callbacks: {
                    label: function (context) {
                        const label = context.dataset.label || '';
                        const value = context.parsed.y;
                        return `${label}: ${value} requests`;
                    },
                    afterBody: function (tooltipItems) {
                        if (!tooltipItems.length) return [];
                        const item = tooltipItems[0];
                        const deviceLabel = item.dataset.label;
                        const hour = item.dataIndex;
                        const key = `${deviceLabel}|${hour}`;
                        const domains = meta[key];
                        if (!domains || !domains.length) return [];
                        const lines = ['', 'Top domains:'];
                        domains.forEach(d => {
                            lines.push(`  ${d.domain} (${d.count})`);
                        });
                        return lines;
                    }
                }
            };
        }

        this.charts[canvasId] = new Chart(canvas, config);
    },

    destroy: function (canvasId) {
        if (this.charts[canvasId]) {
            this.charts[canvasId].destroy();
            delete this.charts[canvasId];
        }
        delete this.metadata[canvasId];
    }
};
