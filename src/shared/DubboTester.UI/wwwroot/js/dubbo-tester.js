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
        const getMaximumWidth = () => {
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
            return Math.max(
                minimumWidth,
                Math.min(bounds.width * .45, bounds.width - historyWidth - 520));
        };
        const setWidthFromClientX = clientX => {
            const bounds = workspace.getBoundingClientRect();
            const width = Math.min(
                getMaximumWidth(),
                Math.max(minimumWidth, clientX - bounds.left));
            workspace.style.setProperty("--catalog-width", `${width}px`);
            return width;
        };
        const savedWidth = Number.parseFloat(window.localStorage.getItem(storageKey));
        if (Number.isFinite(savedWidth) && savedWidth >= minimumWidth) {
            workspace.style.setProperty(
                "--catalog-width",
                `${Math.min(savedWidth, getMaximumWidth())}px`);
        }

        handle.dataset.resizeInitialized = "true";
        handle.addEventListener("pointerdown", event => {
            if (event.button !== 0) return;
            event.preventDefault();
            document.body.classList.add("resizing-catalog");
            const pointerId = event.pointerId;

            const resize = pointerEvent => {
                if (pointerEvent.pointerId !== pointerId) return;
                setWidthFromClientX(pointerEvent.clientX);
            };

            const finish = pointerEvent => {
                if (pointerEvent.pointerId !== pointerId) return;
                resize(pointerEvent);
                window.removeEventListener("pointermove", resize);
                window.removeEventListener("pointerup", finish);
                window.removeEventListener("pointercancel", finish);
                document.body.classList.remove("resizing-catalog");
                const width = Number.parseFloat(
                    getComputedStyle(workspace).getPropertyValue("--catalog-width"));
                if (Number.isFinite(width)) {
                    window.localStorage.setItem(storageKey, width.toString());
                }
            };

            window.addEventListener("pointermove", resize);
            window.addEventListener("pointerup", finish);
            window.addEventListener("pointercancel", finish);
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

// Small anchored popovers should behave like transient UI: clicking anywhere
// outside the active popover, or pressing Escape, dismisses it. The listener is
// delegated so it also covers popovers rendered later by Blazor.
const closeDismissiblePopovers = target => {
    document.querySelectorAll("details[data-dismiss-on-outside][open]").forEach(popover => {
        if (!target || !popover.contains(target)) {
            popover.open = false;
        }
    });
};

document.addEventListener("click", event => closeDismissiblePopovers(event.target));
document.addEventListener("keydown", event => {
    if (event.key === "Escape") {
        closeDismissiblePopovers(null);
    }
});
