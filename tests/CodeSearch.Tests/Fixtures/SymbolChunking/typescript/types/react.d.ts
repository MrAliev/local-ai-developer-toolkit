declare module 'react' {
  export function useMemo<T>(factory: () => T, deps: unknown[]): T;
}

declare namespace JSX {
  interface Element {}
  interface IntrinsicElements {
    [name: string]: unknown;
  }
}
