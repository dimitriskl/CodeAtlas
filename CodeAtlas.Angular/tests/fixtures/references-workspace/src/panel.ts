export class Panel {
  quantity = model(0);
}

export function read(panel: Panel): number {
  return panel.quantity;
}

export function setQuantity(panel: Panel): void {
  panel.quantity = 1;
}
