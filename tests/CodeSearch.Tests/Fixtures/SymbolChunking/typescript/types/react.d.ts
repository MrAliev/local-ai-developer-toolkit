declare module 'react' {
  export function useMemo<T>(factory: () => T, deps: unknown[]): T;
  export function memo<T>(component: T): T;
  export function forwardRef<T>(render: T): T;
}

declare namespace JSX {
  interface Element {}
  interface IntrinsicElements {
    [name: string]: unknown;
  }
}
