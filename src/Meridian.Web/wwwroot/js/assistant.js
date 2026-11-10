// AI assistant panel helpers (loaded as an ES module by AssistantPanel.razor).

// Enter sends (clicks the send button), Shift+Enter inserts a newline.
export function install(textarea, sendButton) {
  if (!textarea || textarea.dataset.aiInstalled) return;
  textarea.dataset.aiInstalled = "1";
  textarea.addEventListener("keydown", e => {
    if (e.key === "Enter" && !e.shiftKey && !e.isComposing) {
      e.preventDefault();
      sendButton?.click();
    }
  });
}

// Keeps the transcript pinned to the newest message unless the user scrolled up.
export function follow(el) {
  if (!el) return;
  const nearEnd = el.scrollHeight - el.scrollTop - el.clientHeight < 120;
  if (nearEnd || el.dataset.aiForce === "1") el.scrollTop = el.scrollHeight;
  el.dataset.aiForce = "";
}

export function toEnd(el) {
  if (el) el.scrollTop = el.scrollHeight;
}

export function focus(el) {
  el?.focus();
}
