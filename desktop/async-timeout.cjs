async function within(promise, milliseconds) {
  let timer;
  try {
    return await Promise.race([
      promise,
      new Promise((_, reject) => {
        timer = setTimeout(() => reject(new Error('界面响应超时。')), milliseconds);
      }),
    ]);
  } finally {
    clearTimeout(timer);
  }
}

module.exports = { within };
