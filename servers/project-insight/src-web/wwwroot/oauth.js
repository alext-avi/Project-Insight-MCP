// A second click on a sign-in button would send a second POST, and the browser would show that
// response instead of the redirect back to the app. Disable the buttons once a form is submitted.
for (const form of document.querySelectorAll("form")) {
  form.addEventListener("submit", () => {
    for (const button of form.querySelectorAll("button")) {
      button.dataset.label = button.textContent;
      button.disabled = true;
      button.textContent = "Checking\u2026";
    }
  });
}
// Coming back with the Back button restores the page as it was left. Re-enable the buttons rather
// than reload: this page may be a POST result, and a reload would offer to send the form again.
window.addEventListener("pageshow", (event) => {
  if (!event.persisted) return;
  for (const button of document.querySelectorAll("button[data-label]")) {
    button.disabled = false;
    button.textContent = button.dataset.label;
  }
});
