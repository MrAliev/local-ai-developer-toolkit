import { BASE_RATE, roundMoney } from './rates';

export { roundMoney } from './rates';

export function applyTax(amount: number): number {
  const tax = amount * BASE_RATE;
  return roundMoney(amount + tax);
}

export class Invoice {
  private readonly lines: number[] = [];

  add(amount: number): void {
    this.lines.push(amount);
  }

  total(): number {
    let sum = 0;
    for (const line of this.lines) {
      sum += line;
    }

    return applyTax(sum);
  }
}

export const describeInvoice = (invoice: Invoice): string => {
  const total = invoice.total();
  return `total ${total}`;
};

console.log(describeInvoice(new Invoice()));
