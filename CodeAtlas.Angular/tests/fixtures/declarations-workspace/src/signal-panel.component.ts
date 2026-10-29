import { Component } from "@angular/core";

@Component({
  selector: "app-signal-panel",
  standalone: true,
  template: "",
})
export class SignalPanel {
  readonly title = input("");

  readonly sku = input.required<string>();

  readonly saved = output<void>();

  readonly quantity = model(0);

  readonly label = model.required<string>();
}
