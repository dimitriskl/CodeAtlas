import { Component, EventEmitter, Input, Output } from "@angular/core";

@Component({
  selector: "app-decorator-panel",
  standalone: true,
  template: "",
})
export class DecoratorPanel {
  @Input() title = "";

  @Input({ required: true }) sku!: string;

  @Output() saved = new EventEmitter<void>();

  @Input() quantity = 0;

  @Output() quantityChange = new EventEmitter<number>();

  @Input({ required: true }) label!: string;

  @Output() labelChange = new EventEmitter<string>();
}
