window.dubboTester = {
    load: key => window.localStorage.getItem(key),
    save: (key, value) => window.localStorage.setItem(key, value),
    loadSession: key => window.sessionStorage.getItem(key),
    saveSession: (key, value) => window.sessionStorage.setItem(key, value),
    initializeCatalogResize: (workspaceSelector, handleSelector, storageKey) => {
        const workspace = document.querySelector(workspaceSelector);
        const handle = document.querySelector(handleSelector);
        if (!workspace || !handle || handle.dataset.resizeInitialized === "true") return;

        const minimumWidth = 220;
        const savedWidth = Number.parseFloat(window.localStorage.getItem(storageKey));
        if (Number.isFinite(savedWidth) && savedWidth >= minimumWidth) {
            workspace.style.setProperty("--catalog-width", `${savedWidth}px`);
        }

        handle.dataset.resizeInitialized = "true";
        handle.addEventListener("pointerdown", event => {
            if (event.button !== 0) return;
            event.preventDefault();
            handle.setPointerCapture(event.pointerId);
            document.body.classList.add("resizing-catalog");

            const resize = pointerEvent => {
                const bounds = workspace.getBoundingClientRect();
                const history = workspace.querySelector(".history-panel");
                const historyBounds = history?.getBoundingClientRect();
                const historySharesRow = historyBounds
                    && Math.abs(historyBounds.top - bounds.top) < 4;
                const historyWidth = !historySharesRow
                    ? 0
                    : history?.classList.contains("collapsed")
                        ? 44
                        : historyBounds.width;
                const maximumWidth = Math.max(
                    minimumWidth,
                    Math.min(bounds.width * .45, bounds.width - historyWidth - 520));
                const width = Math.min(
                    maximumWidth,
                    Math.max(minimumWidth, pointerEvent.clientX - bounds.left));
                workspace.style.setProperty("--catalog-width", `${width}px`);
            };

            const finish = pointerEvent => {
                resize(pointerEvent);
                if (handle.hasPointerCapture(event.pointerId)) {
                    handle.releasePointerCapture(event.pointerId);
                }
                handle.removeEventListener("pointermove", resize);
                handle.removeEventListener("pointerup", finish);
                handle.removeEventListener("pointercancel", finish);
                document.body.classList.remove("resizing-catalog");
                const width = Number.parseFloat(
                    getComputedStyle(workspace).getPropertyValue("--catalog-width"));
                if (Number.isFinite(width)) {
                    window.localStorage.setItem(storageKey, width.toString());
                }
            };

            handle.addEventListener("pointermove", resize);
            handle.addEventListener("pointerup", finish);
            handle.addEventListener("pointercancel", finish);
        });
    },
    copy: value => navigator.clipboard.writeText(value ?? ""),
    download: (fileName, content, contentType = "application/json;charset=utf-8") => {
        const blob = new Blob([content ?? ""], { type: contentType });
        const url = URL.createObjectURL(blob);
        const link = document.createElement("a");
        link.href = url;
        link.download = fileName;
        link.click();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    }
};
