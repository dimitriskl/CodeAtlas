import { Bag, Cart } from "./cart.js";

export function bill(cart: Cart, bag: Bag): number {
  const first = cart.total();
  return cart.total() + cart.total() + bag.total();
}

export function make(): Cart {
  return new Cart();
}
