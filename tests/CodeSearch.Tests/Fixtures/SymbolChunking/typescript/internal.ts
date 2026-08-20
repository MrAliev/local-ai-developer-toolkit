function hiddenHelper(value: number): number {
  return value * 2;
}

class HiddenBox {
  value = 0;

  read(): number {
    return this.value;
  }
}

const hiddenArrow = (value: number) => value + 1;

export const exposed = hiddenHelper(1) + new HiddenBox().read() + hiddenArrow(2);
