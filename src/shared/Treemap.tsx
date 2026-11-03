import {
  useCallback,
  useEffect,
  useLayoutEffect,
  useRef,
  useState,
  forwardRef,
  useImperativeHandle,
} from 'react';
import { createPortal } from 'react-dom';
import type { Entry, MapNode } from './types';
import { bytes } from './api';

const commonColors: Record<string, string> = {
  '.mp4': '#77b9d2',
  '.mov': '#86c6bd',
  '.jpg': '#b9a8d9',
  '.png': '#d5afbf',
  '.zip': '#e1b39d',
  '.7z': '#d9b5a9',
  '.txt': '#c3c7a6',
  '.pdf': '#8fa8ca',
  '.exe': '#a2c4a5',
  '.bin': '#b5bdc5',
};
export function extensionColor(extension: string) {
  if (!extension) return '#b5bdc5';
  const normalized = extension.toLowerCase();
  if (commonColors[normalized]) return commonColors[normalized];
  let hash = 2166136261;
  for (const character of normalized) hash = Math.imul(hash ^ character.charCodeAt(0), 16777619);
  return `hsl(${(hash >>> 0) % 360} 35% 70%)`;
}
interface Rectangle {
  node: MapNode;
  x: number;
  y: number;
  width: number;
  height: number;
  depth: number;
  header: number;
}
export interface TreemapHandle {
  png(): string | null;
}
function layout(
  nodes: MapNode[],
  x: number,
  y: number,
  width: number,
  height: number,
  metric: 'allocated' | 'size',
  depth: number,
  result: Rectangle[],
) {
  const weighted = nodes
    .map((node) => ({
      node,
      weight: metric === 'allocated' ? node.entry.totalAllocated : node.entry.totalSize,
    }))
    .filter((item) => item.weight > 0);
  function divide(items: typeof weighted, x: number, y: number, width: number, height: number) {
    if (!items.length || width < 1 || height < 1) return;
    if (items.length === 1) {
      const node = items[0].node;
      const expand = node.children.length > 0 && width > 24 && height > 24;
      const header = expand && width > 100 && height > 60 ? 18 : 0;
      result.push({ node, x, y, width, height, depth, header });
      if (expand)
        layout(
          node.children,
          x + 2,
          y + header + 2,
          width - 4,
          height - header - 4,
          metric,
          depth + 1,
          result,
        );
      return;
    }
    const total = items.reduce((sum, item) => sum + item.weight, 0);
    let sum = 0,
      split = 0;
    while (split < items.length - 1 && sum < total / 2) sum += items[split++].weight;
    const fraction = sum / total;
    if (width >= height) {
      divide(items.slice(0, split), x, y, width * fraction, height);
      divide(items.slice(split), x + width * fraction, y, width * (1 - fraction), height);
    } else {
      divide(items.slice(0, split), x, y, width, height * fraction);
      divide(items.slice(split), x, y + height * fraction, width, height * (1 - fraction));
    }
  }
  divide(weighted, x, y, width, height);
}

export const Treemap = forwardRef<
  TreemapHandle,
  {
    nodes: MapNode[];
    metric: 'allocated' | 'size';
    selected?: string;
    onSelect: (entry: Entry) => void;
    onOpen: (entry: Entry) => void;
    theme: string;
  }
>(function Treemap({ nodes, metric, selected, onSelect, onOpen, theme }, ref) {
  const canvas = useRef<HTMLCanvasElement>(null);
  const container = useRef<HTMLDivElement>(null);
  const rectangles = useRef<Rectangle[]>([]);
  const [size, setSize] = useState({ width: 600, height: 300 });
  const tooltip = useRef<HTMLDivElement>(null);
  const hoverTimer = useRef<ReturnType<typeof setTimeout> | null>(null);
  const [hover, setHover] = useState<{ rectangle: Rectangle; x: number; y: number } | null>(null);
  const hideHover = useCallback(() => {
    if (hoverTimer.current !== null) clearTimeout(hoverTimer.current);
    hoverTimer.current = null;
    setHover(null);
  }, []);
  useEffect(hideHover, [hideHover, nodes, metric, selected, size, theme]);
  useEffect(() => {
    const escape = (event: KeyboardEvent) => {
      if (event.key === 'Escape') hideHover();
    };
    window.addEventListener('blur', hideHover);
    window.addEventListener('resize', hideHover);
    window.addEventListener('scroll', hideHover, true);
    window.addEventListener('keydown', escape);
    document.addEventListener('visibilitychange', hideHover);
    return () => {
      window.removeEventListener('blur', hideHover);
      window.removeEventListener('resize', hideHover);
      window.removeEventListener('scroll', hideHover, true);
      window.removeEventListener('keydown', escape);
      document.removeEventListener('visibilitychange', hideHover);
      if (hoverTimer.current !== null) clearTimeout(hoverTimer.current);
    };
  }, [hideHover]);
  useLayoutEffect(() => {
    const element = tooltip.current;
    if (!hover || !element) return;
    const bounds = element.getBoundingClientRect();
    const margin = 8,
      gap = 16;
    const left =
      hover.x + gap + bounds.width <= window.innerWidth - margin
        ? hover.x + gap
        : hover.x - gap - bounds.width;
    const top =
      hover.y + gap + bounds.height <= window.innerHeight - margin
        ? hover.y + gap
        : hover.y - gap - bounds.height;
    element.style.left = `${Math.max(margin, Math.min(left, window.innerWidth - bounds.width - margin))}px`;
    element.style.top = `${Math.max(margin, Math.min(top, window.innerHeight - bounds.height - margin))}px`;
    element.style.visibility = 'visible';
  }, [hover]);
  useImperativeHandle(
    ref,
    () => ({ png: () => canvas.current?.toDataURL('image/png') ?? null }),
    [],
  );
  useEffect(() => {
    const observer = new ResizeObserver((entries) => {
      const { width, height } = entries[0].contentRect;
      setSize({ width, height });
    });
    if (container.current) observer.observe(container.current);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    const element = canvas.current;
    const context = element?.getContext('2d');
    if (!element || !context || size.width < 1 || size.height < 1) return;
    const scale = window.devicePixelRatio || 1;
    element.width = Math.round(size.width * scale);
    element.height = Math.round(size.height * scale);
    context.scale(scale, scale);
    context.fillStyle = theme === 'dark' ? '#24272b' : '#ffffff';
    context.fillRect(0, 0, size.width, size.height);
    const result: Rectangle[] = [];
    layout(nodes, 4, 4, size.width - 8, size.height - 8, metric, 0, result);
    rectangles.current = result;
    for (const rectangle of result) {
      const { x, y, width, height, node, header } = rectangle;
      const entry = node.entry;
      const directory = entry.file.directory;
      context.fillStyle = directory
        ? theme === 'dark'
          ? '#39454c'
          : '#dfe8e9'
        : extensionColor(entry.file.extension);
      context.fillRect(x + 0.5, y + 0.5, Math.max(0, width - 1), Math.max(0, height - 1));
      if (width > 65 && (header > 0 || height > 28)) {
        context.save();
        context.beginPath();
        context.rect(x + 6, y + 2, width - 12, header || height - 4);
        context.clip();
        context.fillStyle = directory && theme === 'dark' ? '#f2efea' : '#182f38';
        context.font = '12px "Segoe UI", "Microsoft YaHei UI", sans-serif';
        context.fillText(
          entry.file.name +
            (header
              ? ` (${bytes(metric === 'allocated' ? entry.totalAllocated : entry.totalSize)})`
              : ''),
          x + 7,
          y + (header ? 14 : 20),
        );
        if (!header && height > 49)
          context.fillText(
            bytes(metric === 'allocated' ? entry.totalAllocated : entry.totalSize),
            x + 7,
            y + 39,
          );
        context.restore();
      }
    }
    const selectedRect =
      result.find((rectangle) => rectangle.node.entry.file.path === selected) ??
      [...result]
        .reverse()
        .find(
          (rectangle) =>
            rectangle.node.entry.file.directory &&
            selected?.startsWith(rectangle.node.entry.file.path.replace(/\\$/, '') + '\\'),
        );
    if (selectedRect) {
      context.strokeStyle = theme === 'dark' ? '#ffffff' : '#185c70';
      context.lineWidth = 2;
      context.strokeRect(
        selectedRect.x + 1,
        selectedRect.y + 1,
        selectedRect.width - 2,
        selectedRect.height - 2,
      );
    }
  }, [nodes, metric, selected, size, theme]);
  function hit(x: number, y: number) {
    for (let index = rectangles.current.length - 1; index >= 0; index--) {
      const rectangle = rectangles.current[index];
      if (
        x >= rectangle.x &&
        y >= rectangle.y &&
        x < rectangle.x + rectangle.width &&
        y < rectangle.y + rectangle.height
      )
        return rectangle;
    }
    return null;
  }
  return (
    <div className="treemap" ref={container}>
      <canvas
        ref={canvas}
        role="img"
        aria-label="多层磁盘占用树图，颜色对应文件类型，可用方向键选择项目，Enter 打开目录"
        tabIndex={0}
        onMouseMove={(event) => {
          hideHover();
          if (event.buttons) return;
          const bounds = event.currentTarget.getBoundingClientRect();
          const rectangle = hit(event.clientX - bounds.left, event.clientY - bounds.top);
          if (!rectangle) return;
          const x = event.clientX,
            y = event.clientY;
          hoverTimer.current = setTimeout(() => {
            hoverTimer.current = null;
            setHover({ rectangle, x, y });
          }, 350);
        }}
        onMouseLeave={hideHover}
        onMouseDown={hideHover}
        onBlur={hideHover}
        onClick={(event) => {
          hideHover();
          const bounds = event.currentTarget.getBoundingClientRect();
          const rectangle = hit(event.clientX - bounds.left, event.clientY - bounds.top);
          if (rectangle) onSelect(rectangle.node.entry);
        }}
        onDoubleClick={(event) => {
          hideHover();
          const bounds = event.currentTarget.getBoundingClientRect();
          const rectangle = hit(event.clientX - bounds.left, event.clientY - bounds.top);
          if (rectangle) onOpen(rectangle.node.entry);
        }}
        onKeyDown={(event) => {
          hideHover();
          const choices = rectangles.current.filter((rectangle) => rectangle.node.entry.file.path);
          const index = Math.max(
            0,
            choices.findIndex((rectangle) => rectangle.node.entry.file.path === selected),
          );
          if (event.key === 'Enter' && choices[index]) onOpen(choices[index].node.entry);
          if (['ArrowRight', 'ArrowDown', 'ArrowLeft', 'ArrowUp'].includes(event.key)) {
            event.preventDefault();
            const step = ['ArrowLeft', 'ArrowUp'].includes(event.key) ? -1 : 1;
            const choice = choices[Math.max(0, Math.min(choices.length - 1, index + step))];
            if (choice) onSelect(choice.node.entry);
          }
        }}
      />
      {hover &&
        createPortal(
          <div className="map-tooltip" role="tooltip" ref={tooltip}>
            <strong>{hover.rectangle.node.entry.file.name}</strong>
            <span>
              {bytes(
                metric === 'allocated'
                  ? hover.rectangle.node.entry.totalAllocated
                  : hover.rectangle.node.entry.totalSize,
              )}
            </span>
            {hover.rectangle.node.entry.file.path && (
              <span className="path">{hover.rectangle.node.entry.file.path}</span>
            )}
          </div>,
          document.body,
        )}
      {nodes.length === 0 && <div className="map-empty">此范围没有可显示的占用</div>}
    </div>
  );
});
