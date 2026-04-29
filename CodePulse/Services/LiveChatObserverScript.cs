namespace CodePulse.Services;

public static class LiveChatObserverScript
{
    public static string Get()
    {
        return
            """
            (() => {
                if (window.__codePulseObserverInstalled) {
                    return;
                }

                window.__codePulseObserverInstalled = true;
                const seen = new Set();
                const seenOrder = [];
                const maxSeenKeys = 4000;
                const messageSelector = [
                    "yt-live-chat-text-message-renderer",
                    "yt-live-chat-paid-message-renderer",
                    "yt-live-chat-membership-item-renderer",
                    "yt-live-chat-paid-sticker-renderer"
                ].join(",");
                const endedSigns = [
                    "This live chat has ended",
                    "Chat is disabled",
                    "\u0e44\u0e21\u0e48\u0e2a\u0e32\u0e21\u0e32\u0e23\u0e16\u0e43\u0e0a\u0e49\u0e41\u0e0a\u0e17\u0e44\u0e14\u0e49",
                    "\u0e2a\u0e34\u0e49\u0e19\u0e2a\u0e38\u0e14",
                    "\u0e41\u0e0a\u0e17\u0e19\u0e35\u0e49\u0e2a\u0e34\u0e49\u0e19\u0e2a\u0e38\u0e14\u0e41\u0e25\u0e49\u0e27"
                ];

                const post = (payload) => {
                    try {
                        window.chrome.webview.postMessage(JSON.stringify(payload));
                    } catch {
                    }
                };

                const getContainer = () => document.querySelector("yt-live-chat-item-list-renderer #items");
                const getAuthor = (item) => item.querySelector("#author-name")?.textContent?.trim() || "";
                const getText = (item) => item.querySelector("#message")?.innerText?.trim() || item.innerText?.trim() || "";
                const getTimestamp = (item) => item.querySelector("#timestamp")?.textContent?.trim() || "";
                const rememberKey = (key) => {
                    if (!key || seen.has(key)) {
                        return false;
                    }

                    seen.add(key);
                    seenOrder.push(key);
                    while (seenOrder.length > maxSeenKeys) {
                        const oldestKey = seenOrder.shift();
                        if (oldestKey) {
                            seen.delete(oldestKey);
                        }
                    }

                    return true;
                };
                const isOwner = (item) => {
                    const authorName = item.querySelector("#author-name");
                    const authorType = (item.getAttribute("author-type") || "").toLowerCase();
                    if (authorType === "owner") {
                        return true;
                    }

                    if (authorName) {
                        const type = (authorName.getAttribute("type") || "").toLowerCase();
                        if (type === "owner" || authorName.classList.contains("owner")) {
                            return true;
                        }
                    }

                    const badge = item.querySelector("yt-live-chat-author-badge-renderer");
                    if (badge) {
                        const badgeText = (
                            badge.getAttribute("aria-label") ||
                            badge.getAttribute("title") ||
                            badge.textContent ||
                            ""
                        ).toLowerCase();
                        if (badgeText.includes("owner") || badgeText.includes("\u0e40\u0e08\u0e49\u0e32\u0e02\u0e2d\u0e07")) {
                            return true;
                        }
                    }

                    return false;
                };

                const getKey = (item) => {
                    const id = item.id || item.getAttribute("id") || "";
                    if (id) {
                        return `id:${id}`;
                    }

                    return [
                        getAuthor(item),
                        getText(item),
                        getTimestamp(item)
                    ].join("|");
                };

                const markExisting = () => {
                    document.querySelectorAll(messageSelector).forEach((item) => {
                        rememberKey(getKey(item));
                    });
                };

                const sendHealth = () => {
                    const appExists = !!document.querySelector("yt-live-chat-app");
                    const containerExists = !!getContainer();
                    const bodyText = document.body?.innerText || "";
                    const ended = endedSigns.some((sign) => bodyText.includes(sign));
                    post({
                        type: "health",
                        appExists,
                        containerExists,
                        domHealthy: appExists && containerExists,
                        ended,
                        timestamp: Date.now()
                    });
                };

                const processItem = (item) => {
                    if (!(item instanceof HTMLElement)) {
                        return;
                    }

                    const key = getKey(item);
                    if (!rememberKey(key)) {
                        return;
                    }

                    post({
                        type: "message",
                        key,
                        author: getAuthor(item),
                        text: getText(item),
                        isOwner: isOwner(item),
                        timestamp: Date.now()
                    });
                };

                const processNode = (node) => {
                    if (!(node instanceof HTMLElement)) {
                        return;
                    }

                    if (node.matches(messageSelector)) {
                        processItem(node);
                        return;
                    }

                    node.querySelectorAll?.(messageSelector).forEach(processItem);
                };

                const tryInstall = () => {
                    const container = getContainer();
                    if (!container) {
                        sendHealth();
                        return false;
                    }

                    markExisting();
                    const observer = new MutationObserver((mutations) => {
                        for (const mutation of mutations) {
                            for (const node of mutation.addedNodes) {
                                processNode(node);
                            }
                        }
                    });

                    observer.observe(container, { childList: true, subtree: true });
                    post({
                        type: "ready",
                        skippedCount: seen.size,
                        timestamp: Date.now()
                    });
                    sendHealth();
                    setInterval(sendHealth, 5000);
                    return true;
                };

                if (!tryInstall()) {
                    const timer = setInterval(() => {
                        if (tryInstall()) {
                            clearInterval(timer);
                        }
                    }, 1000);
                }
            })();
            """;
    }
}
