export const BASE_RATE = 0.2;

export function roundMoney(value: number): number {
  return Math.round(value * 100) / 100;
}
