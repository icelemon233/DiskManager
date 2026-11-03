/* global cinnaTray */
const menu = document.getElementById('tray-menu');
let buttons = [];
let current = -1;
let sequence = 0;
let initialRender = false;

function render(state) {
  sequence = state.sequence;
  const root = document.documentElement;
  root.dataset.theme = state.theme;
  root.dataset.highContrast = String(state.highContrast);
  for (const [name, value] of Object.entries(state.contract)) {
    if (typeof value !== 'number') continue;
    const property = name.replace(/[A-Z]/g, (letter) => '-' + letter.toLowerCase());
    root.style.setProperty('--' + property, String(value) + (name === 'iconStroke' ? '' : 'px'));
  }
  root.style.setProperty('--font-size', `${state.contract.fontSize * state.textScale}px`);
  root.style.setProperty('--maximum-width', `${state.maximumWidth}px`);
  root.style.setProperty('--maximum-height', `${state.maximumHeight}px`);
  menu.replaceChildren();
  menu.dataset.input = 'pointer';
  for (const item of state.items) {
    const element = document.createElement(item.id ? 'button' : 'div');
    if (!item.id) {
      element.className = 'tray-separator';
      element.setAttribute('role', 'separator');
    } else {
      element.className = 'tray-item';
      element.type = 'button';
      element.setAttribute('role', 'menuitem');
      element.tabIndex = -1;
      element.dataset.command = item.id;
      const icon = document.createElement('span');
      icon.className = 'tray-icon';
      icon.setAttribute('aria-hidden', 'true');
      icon.innerHTML = state.icons[item.icon] ?? '';
      const label = document.createElement('span');
      label.className = 'tray-label';
      label.textContent = item.label;
      element.append(icon, label);
      element.addEventListener('click', () => cinnaTray.command(item.id));
      element.addEventListener('pointermove', () => {
        menu.dataset.input = 'pointer';
        current = buttons.indexOf(element);
        buttons.forEach((button) => button.classList.remove('keyboard-active'));
        menu.focus({ preventScroll: true });
      });
    }
    menu.append(element);
  }
  buttons = Array.from(menu.querySelectorAll('button'));
  current = -1;
  // The window stays hidden until this measurement reaches the main process.
  // Measure synchronously: a hidden window may not receive animation frames.
  const bounds = menu.getBoundingClientRect();
  cinnaTray.layout({
    sequence,
    width: Math.ceil(bounds.width),
    height: Math.ceil(bounds.height),
  });
}

document.addEventListener('keydown', (event) => {
  if (event.key === 'Escape' || event.key === 'Tab') {
    event.preventDefault();
    cinnaTray.dismiss('keyboard');
    return;
  }
  let next;
  if (event.key === 'ArrowDown') next = (current + 1) % buttons.length;
  if (event.key === 'ArrowUp')
    next = current < 0 ? buttons.length - 1 : (current - 1 + buttons.length) % buttons.length;
  if (event.key === 'Home') next = 0;
  if (event.key === 'End') next = buttons.length - 1;
  if (next !== undefined) {
    menu.dataset.input = 'keyboard';
    event.preventDefault();
    current = next;
    buttons.forEach((button, index) =>
      button.classList.toggle('keyboard-active', index === current),
    );
    buttons[current].focus();
  } else if ((event.key === 'Enter' || event.key === ' ') && current >= 0) {
    event.preventDefault();
    buttons[current].click();
  }
});
document.addEventListener('pointerdown', (event) => {
  if (!menu.contains(event.target)) cinnaTray.dismiss('pointer');
});
document.addEventListener('contextmenu', (event) => event.preventDefault());
cinnaTray.onRender((state) => {
  initialRender = true;
  render(state);
});
cinnaTray.onFocus(() => menu.focus({ preventScroll: true }));
void cinnaTray.state().then((state) => {
  if (!initialRender) render(state);
});
