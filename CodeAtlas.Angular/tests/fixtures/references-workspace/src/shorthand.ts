import { Cart } from "./cart.js";

export function take(cart: Cart): number {
  const { total } = cart;
  return total();
}

export function read({ total }: Cart): number {
  return total();
}

export function assign(cart: Cart): void {
  let total: () => number;
  ({ total } = cart);
  void total;
}
