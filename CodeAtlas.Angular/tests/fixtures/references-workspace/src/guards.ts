export const authGuard: CanActivateFn = () => true;

export function allow(): boolean {
  return authGuard();
}

export function pass(): CanActivateFn {
  return authGuard;
}
