const keyPrefix = "dxgrid.widths.";
const minimumWidth = 60;

function load(key) {
    try {
        return JSON.parse(localStorage.getItem(keyPrefix + key) || "{}");
    } catch {
        return {};
    }
}

function save(key, widths) {
    localStorage.setItem(keyPrefix + key, JSON.stringify(widths));
}

function attach(root) {
    const key = root.dataset.gridResizeKey;
    if (!key || root.dataset.gridResizeAttached === "true") return;
    root.dataset.gridResizeAttached = "true";
    const widths = load(key);

    const apply = () => {
        const headers = [...root.querySelectorAll("thead th")];
        if (headers.length < 2) return;
        headers.forEach((header, index) => {
            if (widths[index]) header.style.width = `${widths[index]}px`;
            if (index === headers.length - 1 || header.querySelector(".dx-grid-resize-handle")) return;
            const handle = document.createElement("span");
            handle.className = "dx-grid-resize-handle";
            handle.setAttribute("aria-label", "Resize column");
            handle.addEventListener("pointerdown", event => {
                event.preventDefault();
                event.stopPropagation();
                const startX = event.clientX;
                const startWidth = header.getBoundingClientRect().width;
                handle.setPointerCapture(event.pointerId);
                root.classList.add("dx-grid-resizing");
                const move = moveEvent => {
                    const width = Math.max(minimumWidth, startWidth + moveEvent.clientX - startX);
                    header.style.width = `${width}px`;
                    widths[index] = Math.round(width);
                };
                const end = () => {
                    root.classList.remove("dx-grid-resizing");
                    save(key, widths);
                    handle.removeEventListener("pointermove", move);
                    handle.removeEventListener("pointerup", end);
                    handle.removeEventListener("pointercancel", end);
                };
                handle.addEventListener("pointermove", move);
                handle.addEventListener("pointerup", end);
                handle.addEventListener("pointercancel", end);
            });
            handle.addEventListener("dblclick", event => {
                event.preventDefault();
                delete widths[index];
                header.style.width = "";
                save(key, widths);
            });
            header.appendChild(handle);
        });
    };

    apply();
    const observer = new MutationObserver(apply);
    observer.observe(root, { childList: true, subtree: true });
    root._gridResizeDispose = () => observer.disconnect();
}

function attachAll() {
    document.querySelectorAll("[data-grid-resize-key]").forEach(attach);
}

window.downloadFile = (contentType, bytes, fileName) => {
    const data = bytes instanceof Uint8Array ? bytes : new Uint8Array(bytes);
    const blob = new Blob([data], { type: contentType || "application/octet-stream" });
    const url = URL.createObjectURL(blob);
    const link = document.createElement("a");
    link.href = url;
    link.download = fileName || "download.bin";
    link.click();
    URL.revokeObjectURL(url);
};

window.dxGridResize = { attachAll };
new MutationObserver(attachAll).observe(document.body, { childList: true, subtree: true });
attachAll();
