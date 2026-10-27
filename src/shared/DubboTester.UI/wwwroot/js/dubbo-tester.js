const dubboTesterDatabaseName = "DubboTester";
const dubboTesterDatabaseVersion = 1;
const dubboTesterStores = {
    history: "invocationHistory",
    requestDrafts: "requestDrafts",
    pressureDrafts: "pressureDrafts",
    metadata: "metadata"
};

let dubboTesterDatabasePromise;

const openDubboTesterDatabase = () => {
    if (!window.indexedDB) {
        return Promise.reject(new Error("当前运行环境不支持本地数据库存储（IndexedDB）"));
    }

    if (dubboTesterDatabasePromise) return dubboTesterDatabasePromise;

    dubboTesterDatabasePromise = new Promise((resolve, reject) => {
        const request = window.indexedDB.open(
            dubboTesterDatabaseName,
            dubboTesterDatabaseVersion);

        request.onupgradeneeded = () => {
            const database = request.result;
            let historyStore;
            if (!database.objectStoreNames.contains(dubboTesterStores.history)) {
                historyStore = database.createObjectStore(
                    dubboTesterStores.history,
                    { keyPath: "id" });
            } else {
                historyStore = request.transaction.objectStore(dubboTesterStores.history);
            }

            const historyIndexes = [
                ["createdAt", "createdAt"],
                ["serviceName", "serviceName"],
                ["methodName", "methodName"],
                ["success", "success"],
                ["favorite", "favorite"]
            ];
            for (const [name, keyPath] of historyIndexes) {
                if (!historyStore.indexNames.contains(name)) {
                    historyStore.createIndex(name, keyPath, { unique: false });
                }
            }

            if (!database.objectStoreNames.contains(dubboTesterStores.requestDrafts)) {
                database.createObjectStore(
                    dubboTesterStores.requestDrafts,
                    { keyPath: "key" });
            }
            if (!database.objectStoreNames.contains(dubboTesterStores.pressureDrafts)) {
                database.createObjectStore(
                    dubboTesterStores.pressureDrafts,
                    { keyPath: "key" });
            }
            if (!database.objectStoreNames.contains(dubboTesterStores.metadata)) {
                database.createObjectStore(
                    dubboTesterStores.metadata,
                    { keyPath: "key" });
            }
        };

        request.onsuccess = () => {
            const database = request.result;
            database.onversionchange = () => database.close();
            resolve(database);
        };
        request.onerror = () => reject(request.error ?? new Error("打开 IndexedDB 失败"));
        request.onblocked = () => reject(new Error("IndexedDB 升级被其他页面阻塞，请关闭旧页面后重试"));
    }).catch(error => {
        dubboTesterDatabasePromise = undefined;
        throw error;
    });

    return dubboTesterDatabasePromise;
};

const indexedDbRequest = request => new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error("IndexedDB 请求失败"));
});

const indexedDbTransaction = transaction => new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onerror = () => reject(transaction.error ?? new Error("IndexedDB 事务失败"));
    transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB 事务已取消"));
});

const readIndexedDbRecord = async (storeName, key) => {
    const database = await openDubboTesterDatabase();
    const transaction = database.transaction(storeName, "readonly");
    return indexedDbRequest(transaction.objectStore(storeName).get(key));
};

const writeIndexedDbRecord = async (storeName, value) => {
    const database = await openDubboTesterDatabase();
    const transaction = database.transaction(storeName, "readwrite");
    transaction.objectStore(storeName).put(value);
    await indexedDbTransaction(transaction);
};

const historySearchText = value => `${value ?? ""}`.toLocaleLowerCase();

const historyMatchesSearch = (item, searchText) => {
    if (!searchText) return true;

    const routeText = Number(item?.route) === 0 ? "注册中心" : "直连 provider";
    const values = [
        `${item?.serviceName ?? ""}#${item?.methodName ?? ""}`,
        item?.requestJson,
        item?.response,
        item?.javaParameterTypes,
        item?.attachmentsJson,
        item?.registryRootPath,
        item?.host,
        item?.port,
        item?.version,
        item?.group,
        item?.elapsedMs,
        item?.createdAt,
        item?.success ? "成功" : "失败",
        routeText
    ];
    return values.some(value => historySearchText(value).includes(searchText));
};

const historySummary = item => ({
    ...item,
    // The list only needs a short request preview. Full request, attachments and
    // response are fetched by id when the user opens or loads a record.
    requestJson: `${item?.requestJson ?? ""}`.slice(0, 200),
    attachmentsJson: "",
    response: ""
});

const deleteOldestHistoryRecords = (store, maximum, favorite) =>
    new Promise((resolve, reject) => {
        if (maximum <= 0) {
            resolve(0);
            return;
        }

        let removed = 0;
        const request = store.index("createdAt").openCursor(null, "next");
        request.onerror = () => reject(request.error ?? new Error("清理调用历史失败"));
        request.onsuccess = () => {
            const cursor = request.result;
            if (!cursor || removed >= maximum) {
                resolve(removed);
                return;
            }
            if ((cursor.value?.favorite === true) === favorite) {
                cursor.delete();
                removed++;
            }
            cursor.continue();
        };
    });

const trimHistoryStore = async (store, configuredLimit) => {
    const limit = Math.max(10, Math.min(50000, Number(configuredLimit) || 5000));
    const count = await indexedDbRequest(store.count());
    if (count <= limit) return;

    const removeCount = count - limit;
    const removedNonFavorites = await deleteOldestHistoryRecords(store, removeCount, false);
    await deleteOldestHistoryRecords(store, removeCount - removedNonFavorites, true);
};

const migrateLegacyHistory = async legacyStorageKey => {
    const migrationKey = "migration.history.local-storage.v1";
    const migration = await readIndexedDbRecord(dubboTesterStores.metadata, migrationKey);
    const legacyJson = window.localStorage.getItem(legacyStorageKey);
    if (migration && !legacyJson) return;

    const legacyItems = legacyJson ? JSON.parse(legacyJson) : [];
    if (!Array.isArray(legacyItems)) {
        throw new Error("旧版调用历史格式无效，未执行迁移");
    }

    const database = await openDubboTesterDatabase();
    const transaction = database.transaction(
        [dubboTesterStores.history, dubboTesterStores.metadata],
        "readwrite");
    const historyStore = transaction.objectStore(dubboTesterStores.history);
    for (const item of legacyItems) {
        if (item?.id) historyStore.put(item);
    }
    transaction.objectStore(dubboTesterStores.metadata).put({
        key: migrationKey,
        migratedAt: new Date().toISOString(),
        itemCount: legacyItems.length
    });
    await indexedDbTransaction(transaction);
    window.localStorage.removeItem(legacyStorageKey);
};

const migrateLegacyRequestDrafts = async legacySessionStorageKey => {
    const legacyJson = window.sessionStorage.getItem(legacySessionStorageKey);
    if (!legacyJson) return;

    const legacyDrafts = JSON.parse(legacyJson);
    if (!legacyDrafts || Array.isArray(legacyDrafts) || typeof legacyDrafts !== "object") {
        throw new Error("旧版请求参数草稿格式无效，未执行迁移");
    }

    const database = await openDubboTesterDatabase();
    const transaction = database.transaction(dubboTesterStores.requestDrafts, "readwrite");
    const draftStore = transaction.objectStore(dubboTesterStores.requestDrafts);
    const updatedAt = new Date().toISOString();
    for (const [key, value] of Object.entries(legacyDrafts)) {
        draftStore.put({ key, value: value ?? "", updatedAt });
    }
    await indexedDbTransaction(transaction);
    window.sessionStorage.removeItem(legacySessionStorageKey);
};

window.dubboTester = {
    load: key => window.localStorage.getItem(key),
    save: (key, value) => window.localStorage.setItem(key, value),
    indexedDb: {
        initialize: async (legacyHistoryKey, legacyRequestDraftsKey) => {
            await migrateLegacyHistory(legacyHistoryKey);
            await migrateLegacyRequestDrafts(legacyRequestDraftsKey);
            if (navigator.storage?.persist) {
                try {
                    await navigator.storage.persist();
                } catch {
                    // Persistence is best effort; IndexedDB remains usable when denied.
                }
            }
        },
        loadHistoryPage: async (pageNumber, pageSize, searchText, favoritesOnly) => {
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readonly");
            const store = transaction.objectStore(dubboTesterStores.history);
            const index = store.index("createdAt");
            const requestedPage = Math.max(1, Number(pageNumber) || 1);
            const requestedPageSize = Math.max(1, Math.min(100, Number(pageSize) || 100));
            const offset = (requestedPage - 1) * requestedPageSize;
            const normalizedSearch = historySearchText(searchText).trim();
            const items = [];
            let totalCount = 0;

            await new Promise((resolve, reject) => {
                const request = index.openCursor(null, "prev");
                request.onerror = () => reject(request.error ?? new Error("读取调用历史失败"));
                request.onsuccess = () => {
                    const cursor = request.result;
                    if (!cursor) {
                        resolve();
                        return;
                    }

                    const item = cursor.value;
                    const matches = (!favoritesOnly || item?.favorite === true)
                        && historyMatchesSearch(item, normalizedSearch);
                    if (matches) {
                        if (totalCount >= offset && items.length < requestedPageSize) {
                            items.push(historySummary(item));
                        }
                        totalCount++;
                    }
                    cursor.continue();
                };
            });

            return JSON.stringify({ items, totalCount });
        },
        getHistoryMetadata: async id => {
            const item = await readIndexedDbRecord(dubboTesterStores.history, id);
            return item ? JSON.stringify({
                ...item,
                requestJson: "",
                attachmentsJson: "",
                response: ""
            }) : null;
        },
        getHistoryFieldChunk: async (id, fieldName, offset, length) => {
            if (!["requestJson", "attachmentsJson", "response"].includes(fieldName)) {
                throw new Error("不支持读取该调用历史字段");
            }
            const item = await readIndexedDbRecord(dubboTesterStores.history, id);
            const value = `${item?.[fieldName] ?? ""}`;
            const start = Math.max(0, Number(offset) || 0);
            const size = Math.max(1, Math.min(16384, Number(length) || 16384));
            return value.slice(start, start + size);
        },
        trimHistory: async configuredLimit => {
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            await trimHistoryStore(transaction.objectStore(dubboTesterStores.history), configuredLimit);
            await indexedDbTransaction(transaction);
        },
        putHistory: async (json, configuredLimit) => {
            const item = JSON.parse(json);
            if (!item?.id) throw new Error("调用历史缺少 id");
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            const store = transaction.objectStore(dubboTesterStores.history);
            store.put(item);
            await trimHistoryStore(store, configuredLimit);
            await indexedDbTransaction(transaction);
        },
        setHistoryFavorite: async (id, favorite) => {
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            const store = transaction.objectStore(dubboTesterStores.history);
            const item = await indexedDbRequest(store.get(id));
            if (item) store.put({ ...item, favorite: favorite === true });
            await indexedDbTransaction(transaction);
        },
        applyHistoryMutation: async (json, removedIds, configuredLimit) => {
            const item = json ? JSON.parse(json) : null;
            if (item && !item.id) throw new Error("调用历史缺少 id");
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            const store = transaction.objectStore(dubboTesterStores.history);
            for (const id of removedIds ?? []) store.delete(id);
            if (item) store.put(item);
            await trimHistoryStore(store, configuredLimit);
            await indexedDbTransaction(transaction);
        },
        deleteHistory: async ids => {
            if (!Array.isArray(ids) || ids.length === 0) return;
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            const store = transaction.objectStore(dubboTesterStores.history);
            for (const id of ids) store.delete(id);
            await indexedDbTransaction(transaction);
        },
        clearHistory: async () => {
            const database = await openDubboTesterDatabase();
            const transaction = database.transaction(dubboTesterStores.history, "readwrite");
            transaction.objectStore(dubboTesterStores.history).clear();
            await indexedDbTransaction(transaction);
        },
        getRequestDraft: async key => {
            const record = await readIndexedDbRecord(dubboTesterStores.requestDrafts, key);
            return record?.value ?? null;
        },
        putRequestDraft: async (key, value) => {
            await writeIndexedDbRecord(dubboTesterStores.requestDrafts, {
                key,
                value: value ?? "",
                updatedAt: new Date().toISOString()
            });
        },
        loadHomeRequestDraft: async () => {
            const record = await readIndexedDbRecord(dubboTesterStores.metadata, "homeRequestDraft");
            return record?.value ?? null;
        },
        saveHomeRequestDraft: async json => {
            await writeIndexedDbRecord(dubboTesterStores.metadata, {
                key: "homeRequestDraft",
                value: json,
                updatedAt: new Date().toISOString()
            });
        },
        loadPressureDraft: async () => {
            const record = await readIndexedDbRecord(dubboTesterStores.pressureDrafts, "current");
            return record?.value ?? null;
        },
        savePressureDraft: async json => {
            await writeIndexedDbRecord(dubboTesterStores.pressureDrafts, {
                key: "current",
                value: json,
                updatedAt: new Date().toISOString()
            });
        }
    },
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
    },
    downloadUrl: url => {
        const link = document.createElement("a");
        link.href = url;
        link.style.display = "none";
        document.body.appendChild(link);
        link.click();
        link.remove();
    },
    positionPopover: (anchorId, preferredWidth = 420, preferredHeight = 520) => {
        const anchor = document.getElementById(anchorId);
        const margin = 12;
        const gap = 12;
        const topLimit = 84;
        const viewportWidth = window.innerWidth;
        const viewportHeight = window.innerHeight;
        const width = Math.max(260, Math.min(preferredWidth, viewportWidth - margin * 2));
        const height = Math.max(
            240,
            Math.min(preferredHeight, viewportHeight - topLimit - margin));
        const bounds = anchor?.getBoundingClientRect() ?? {
            left: viewportWidth - margin,
            right: viewportWidth - margin,
            top: topLimit,
            height: 48
        };
        const anchorY = bounds.top + bounds.height / 2;
        let left = bounds.left - gap - width;
        let arrowSide = "right";

        if (left < margin) {
            left = bounds.right + gap;
            arrowSide = "left";
        }
        left = Math.max(margin, Math.min(left, viewportWidth - width - margin));

        const top = Math.max(
            topLimit,
            Math.min(anchorY - 34, viewportHeight - height - margin));
        const arrowTop = Math.max(18, Math.min(anchorY - top - 7, height - 32));

        return { left, top, width, height, arrowTop, arrowSide };
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
