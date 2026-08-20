import { useMemo } from 'react';
import { Invoice } from './shapes';

export function InvoicePanel(props: { invoice: Invoice; label: string }) {
  function formatLabel(label: string): string {
    const trimmed = label.trim();
    return trimmed.length === 0 ? 'unnamed' : trimmed;
  }

  const total = useMemo(() => props.invoice.total(), [props.invoice]);
  const caption = formatLabel(props.label);

  return (
    <section>
      <h2>{caption}</h2>
      <span>{total}</span>
    </section>
  );
}

export const emptyPanel = () => <section />;

const bootstrapped = true;

export function OuterPanel() {
  const InnerBadge = () => <span />;

  function innerHelper(): number {
    return 1;
  }

  class InnerStore {
    read(): number {
      return innerHelper();
    }
  }

  return (
    <div>
      <InnerBadge />
      {new InnerStore().read()}
    </div>
  );
}
