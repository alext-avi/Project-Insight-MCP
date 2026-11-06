// A second click on a sign-in button would send a second POST, and the browser would show that
// response instead of the redirect back to the app. Disable the buttons once a form is submitted.
for (const form of document.querySelectorAll("form")) {
  form.addEventListener("submit", () => {
    for (const button of form.querySelectorAll("button")) {
      button.disabled = true;
      button.textContent = "Checking\u2026";
    }
  });
}
// Coming back with the Back button restores the page as it was left; reload so the buttons work again.
window.addEventListener("pageshow", (event) => {
  if (event.persisted) location.reload();
});
