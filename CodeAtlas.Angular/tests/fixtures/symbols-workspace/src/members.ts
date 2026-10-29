export class Cart {
  total(): number {
    return 1;
  }

  price = 1;
}

export interface Line {
  quantity: number;
}

export enum CartStatus {
  Open,
}

export type CartTotal = number;

export namespace Shop {
  export class Item {
    label = "";
  }
}

export class Factory {
  static create(): Factory {
    return new Factory();
  }
}
