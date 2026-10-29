export class Cart {
  total(): number {
    return 1;
  }

  double(): number {
    return this.total() * 2;
  }
}

export class Bag {
  total(): number {
    return 2;
  }
}
