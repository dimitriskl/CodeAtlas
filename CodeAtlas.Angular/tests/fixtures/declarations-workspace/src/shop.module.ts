import { NgModule } from "@angular/core";
import { AuthGuard, AuthInterceptor } from "./auth";
import { LegacyCartService } from "./legacy-cart.service";
import { LegacyHighlightDirective } from "./legacy-highlight.directive";
import { LegacyPricePipe } from "./legacy-price.pipe";
import { Cart } from "./module-cart.component";

@NgModule({
  declarations: [Cart, LegacyHighlightDirective, LegacyPricePipe],
  providers: [LegacyCartService, AuthGuard, AuthInterceptor],
})
export class ShopModule {}
