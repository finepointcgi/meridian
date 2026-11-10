// @mention autocomplete for comment boxes: reports the "@query" at the caret to .NET and inserts the chosen member.
export function attach(el, dotnet) {
  if (!el || el.__mentions) return;
  el.__mentions = true;
  const report = () => {
    const pos = el.selectionStart ?? el.value.length;
    const m = /(^|[^\w@])@([A-Za-z0-9._-]{0,40})$/.exec(el.value.slice(0, pos));
    if (m) dotnet.invokeMethodAsync("MentionQuery", m[2], pos - m[2].length - 1, pos);
    else if (el.dataset.mentionOpen === "1") dotnet.invokeMethodAsync("MentionQuery", null, 0, 0);
  };
  el.addEventListener("input", report);
  el.addEventListener("click", report);
  el.addEventListener("keyup", e => { if (["ArrowLeft", "ArrowRight", "Home", "End"].includes(e.key)) report(); });
  el.addEventListener("keydown", e => {
    if (el.dataset.mentionOpen === "1" && ["ArrowDown", "ArrowUp", "Enter", "Tab", "Escape"].includes(e.key)) {
      e.preventDefault();
      dotnet.invokeMethodAsync("MentionKey", e.key);
    } else if (e.key === "Enter" && (e.metaKey || e.ctrlKey)) {
      e.preventDefault();
      dotnet.invokeMethodAsync("MentionSubmit");
    }
  });
  el.addEventListener("blur", () => setTimeout(() => { if (el.dataset.mentionOpen === "1") dotnet.invokeMethodAsync("MentionQuery", null, 0, 0); }, 180));
}

export function insert(el, start, end, text) {
  if (!el) return;
  el.value = el.value.slice(0, start) + text + el.value.slice(end);
  const caret = start + text.length;
  el.focus();
  el.setSelectionRange(caret, caret);
  el.dispatchEvent(new Event("input", { bubbles: true }));
}
