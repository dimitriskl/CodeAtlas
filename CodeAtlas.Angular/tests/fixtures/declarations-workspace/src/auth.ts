import { HttpInterceptor, HttpInterceptorFn } from "@angular/common/http";
import { Injectable } from "@angular/core";
import { CanActivate, CanActivateFn } from "@angular/router";

@Injectable()
export class AuthGuard implements CanActivate {
  canActivate(): boolean {
    return true;
  }
}

@Injectable()
export class AuthInterceptor implements HttpInterceptor {
  intercept(): unknown {
    return null;
  }
}

export const authGuard: CanActivateFn = () => true;

export const authInterceptor: HttpInterceptorFn = () => null;

export class PlainCart {
  canActivate(): boolean {
    return false;
  }
}

export function notAGuard(): boolean {
  return false;
}

export const notAGuardValue = 1;
