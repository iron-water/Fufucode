// Fufucode Content Collector - 页面文本与选区提取器
(function () {
  function getSelectedText() {
    const selection = window.getSelection();
    if (selection && selection.rangeCount > 0) {
      return selection.toString().trim();
    }
    return "";
  }

  function getPageExcerpt() {
    const selected = getSelectedText();
    if (selected) {
      return `【网页划选片段】\n${selected.slice(0, 12000)}`;
    }

    const article = document.querySelector("article") || document.querySelector("main") || document.body;
    const text = article ? (article.innerText || "").trim().slice(0, 12000) : "";
    return `【网页正文节选】\n${text}`;
  }

  chrome.runtime.onMessage.addListener((msg, sender, sendResponse) => {
    if (msg.type === "EXTRACT_PAGE") {
      sendResponse({
        title: document.title,
        url: window.location.href,
        content: getPageExcerpt(),
        hasSelection: !!getSelectedText()
      });
    }
  });
})();
