import { useEffect, useRef, useState, type ReactNode, type CSSProperties } from 'react';
import * as SelectPrimitive from '@radix-ui/react-select';
import { Check, ChevronDown, ChevronLeft, ChevronRight, X } from 'lucide-react';

export function Select({
  value,
  onChange,
  options,
  label,
  disabled = false,
  onOpenChange,
  icon,
}: {
  value: string;
  onChange: (value: string) => void;
  options: { value: string; label: string; disabled?: boolean }[];
  label: string;
  disabled?: boolean;
  onOpenChange?: (open: boolean) => void;
  icon?: ReactNode;
}) {
  return (
    <SelectPrimitive.Root
      value={value}
      onValueChange={onChange}
      disabled={disabled}
      onOpenChange={onOpenChange}
    >
      <SelectPrimitive.Trigger className="select-trigger" aria-label={label}>
        {icon}
        <SelectPrimitive.Value placeholder={label} />
        <SelectPrimitive.Icon>
          <ChevronDown size={16} />
        </SelectPrimitive.Icon>
      </SelectPrimitive.Trigger>
      <SelectPrimitive.Portal>
        <SelectPrimitive.Content className="select-menu" position="popper" sideOffset={4}>
          <SelectPrimitive.Viewport>
            {options.map((option) => (
              <SelectPrimitive.Item
                className="select-item"
                value={option.value}
                key={option.value}
                disabled={option.disabled}
              >
                {icon}
                <SelectPrimitive.ItemText>{option.label}</SelectPrimitive.ItemText>
                <SelectPrimitive.ItemIndicator>
                  <Check size={16} />
                </SelectPrimitive.ItemIndicator>
              </SelectPrimitive.Item>
            ))}
          </SelectPrimitive.Viewport>
        </SelectPrimitive.Content>
      </SelectPrimitive.Portal>
    </SelectPrimitive.Root>
  );
}

export function Modal({
  title,
  children,
  onClose,
  wide = false,
}: {
  title: string;
  children: ReactNode;
  onClose: () => void;
  wide?: boolean;
}) {
  const dialog = useRef<HTMLDialogElement>(null);
  useEffect(() => {
    const previous = document.activeElement as HTMLElement | null;
    dialog.current?.showModal();
    return () => previous?.focus();
  }, []);
  return (
    <dialog
      ref={dialog}
      className={wide ? 'modal wide' : 'modal'}
      onCancel={(event) => {
        event.preventDefault();
        onClose();
      }}
      onClick={(event) => {
        if (event.target === dialog.current) onClose();
      }}
    >
      <div className="modal-heading">
        <h2>{title}</h2>
        <button className="icon-button" aria-label="关闭对话框" onClick={onClose}>
          <X size={18} />
        </button>
      </div>
      {children}
    </dialog>
  );
}

export function VirtualList<T>({
  items,
  rowHeight = 36,
  render,
  className = '',
  style,
  label,
}: {
  items: T[];
  rowHeight?: number;
  render: (item: T, index: number) => ReactNode;
  className?: string;
  style?: CSSProperties;
  label?: string;
}) {
  const viewport = useRef<HTMLDivElement>(null);
  const [range, setRange] = useState({ top: 0, height: 400 });
  useEffect(() => {
    const element = viewport.current;
    if (!element) return;
    const observer = new ResizeObserver(() =>
      setRange((current) => ({ ...current, height: element.clientHeight })),
    );
    observer.observe(element);
    return () => observer.disconnect();
  }, []);
  useEffect(() => {
    if (viewport.current) viewport.current.scrollTop = 0;
    setRange((current) => ({ ...current, top: 0 }));
  }, [items]);
  const start = Math.max(0, Math.floor(range.top / rowHeight) - 5);
  const end = Math.min(items.length, Math.ceil((range.top + range.height) / rowHeight) + 5);
  return (
    <div
      ref={viewport}
      className={`virtual-list ${className}`}
      style={style}
      role="region"
      aria-label={label}
      tabIndex={0}
      onScroll={(event) =>
        setRange({ top: event.currentTarget.scrollTop, height: event.currentTarget.clientHeight })
      }
    >
      <div style={{ height: items.length * rowHeight, position: 'relative' }}>
        <div style={{ position: 'absolute', top: start * rowHeight, left: 0, right: 0 }}>
          {items.slice(start, end).map((item, index) => (
            <div className="virtual-row" style={{ height: rowHeight }} key={start + index}>
              {render(item, start + index)}
            </div>
          ))}
        </div>
      </div>
    </div>
  );
}

export function Pager({
  offset,
  count,
  onChange,
  size = 200,
}: {
  offset: number;
  count: number;
  onChange: (offset: number) => void;
  size?: number;
}) {
  return (
    <div className="pager">
      <span>
        {count.toLocaleString()} 项
        {count > 0 && ` · ${offset + 1}–${Math.min(count, offset + size)}`}
      </span>
      <button
        className="icon-button"
        disabled={offset === 0}
        aria-label="上一页"
        onClick={() => onChange(Math.max(0, offset - size))}
      >
        <ChevronLeft size={16} />
      </button>
      <button
        className="icon-button"
        disabled={offset + size >= count}
        aria-label="下一页"
        onClick={() => onChange(offset + size)}
      >
        <ChevronRight size={16} />
      </button>
    </div>
  );
}

export function Empty({ children }: { children: ReactNode }) {
  return <div className="empty">{children}</div>;
}

export function Splitter({
  direction,
  onChange,
  value,
  minimum,
  maximum,
  label,
}: {
  direction: 'horizontal' | 'vertical';
  onChange: (value: number) => void;
  value: number;
  minimum: number;
  maximum: number;
  label: string;
}) {
  const origin = useRef<{ coordinate: number; value: number } | null>(null);
  const horizontal = direction === 'horizontal';
  return (
    <div
      className={`splitter ${direction}`}
      role="separator"
      tabIndex={0}
      aria-label={label}
      aria-orientation={horizontal ? 'horizontal' : 'vertical'}
      aria-valuemin={minimum}
      aria-valuemax={maximum}
      aria-valuenow={value}
      onKeyDown={(event) => {
        if (['ArrowLeft', 'ArrowUp', 'ArrowRight', 'ArrowDown'].includes(event.key)) {
          event.preventDefault();
          onChange(
            Math.min(
              maximum,
              Math.max(minimum, value + (['ArrowLeft', 'ArrowUp'].includes(event.key) ? -16 : 16)),
            ),
          );
        }
      }}
      onPointerDown={(event) => {
        origin.current = { coordinate: horizontal ? event.clientY : event.clientX, value };
        event.currentTarget.setPointerCapture(event.pointerId);
      }}
      onPointerMove={(event) => {
        if (origin.current)
          onChange(
            Math.min(
              maximum,
              Math.max(
                minimum,
                origin.current.value +
                  (horizontal ? event.clientY : event.clientX) -
                  origin.current.coordinate,
              ),
            ),
          );
      }}
      onPointerUp={() => {
        origin.current = null;
      }}
      onLostPointerCapture={() => {
        origin.current = null;
      }}
    >
      <span />
    </div>
  );
}
