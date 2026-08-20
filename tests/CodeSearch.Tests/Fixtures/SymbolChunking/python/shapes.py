"""Fixture covering the definition shapes symbol-level chunking has to survive."""

import functools

BASE_RATE = 0.2


def round_money(value: float) -> float:
    return round(value * 100) / 100


def apply_tax(amount: float) -> float:
    def with_rate(rate: float) -> float:
        return amount * (1 + rate)

    return round_money(with_rate(BASE_RATE))


class Invoice:
    def __init__(self) -> None:
        self.lines: list[float] = []

    def add(self, amount: float) -> None:
        self.lines.append(amount)

    def total(self) -> float:
        total = 0.0
        for line in self.lines:
            total += line

        return apply_tax(total)


@functools.lru_cache(maxsize=8)
def describe(amount: float) -> str:
    return f"total {apply_tax(amount)}"


INVOICE = Invoice()
INVOICE.add(10.0)
print(describe(INVOICE.total()))
