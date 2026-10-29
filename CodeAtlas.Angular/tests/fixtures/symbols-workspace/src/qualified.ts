export class CaseBox {
  #hidden = 1;

  get label(): string {
    return "";
  }

  set label(value: string) {}

  constructor(public readonly owner: string) {}
}

export interface OnlyIface {
  ack(): void;
}
