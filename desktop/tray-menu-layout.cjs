function placeMenu(size, anchor, area) {
  const width = Math.min(Math.ceil(size.width), area.width);
  const height = Math.min(Math.ceil(size.height), area.height);
  return {
    x: Math.round(Math.max(area.x, Math.min(anchor.x, area.x + area.width - width))),
    y: Math.round(Math.max(area.y, Math.min(anchor.y, area.y + area.height - height))),
    width,
    height,
  };
}

module.exports = { placeMenu };
