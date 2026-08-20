using System.Globalization;
using System.Text;

namespace LocalAi.TestSupport;

/// <summary>
/// Writes a TypeScript and Python repository that exists to be chunked and searched.
/// </summary>
/// <remarks>
/// The corpus that measured symbol-aware chunking belongs to a repository this project does not
/// own, so the numbers could be committed and the cases could not, and nothing about the
/// measurement was reproducible here. This is the part that can be: a repository generated the
/// same way every time, with a committed set of questions whose answers live in it.
///
/// Two properties are load-bearing, and both are about the corpus being a fair test rather than a
/// large one.
///
/// The shapes are the shapes the chunker actually has to tell apart. A function declaration the
/// indexer reports a body for; a component whose initialiser is a call, which it does not; a
/// one-line constant; an object literal whose every property the indexer names but which must not
/// become a vector each; a class with methods; a handler declared inside a component body. A
/// corpus of one shape would grade nothing.
///
/// The answers hide among near-neighbours. Every generated feature is written from the same
/// templates, so a query cannot be answered by being the only file with the right words in it —
/// which is exactly how a synthetic corpus flatters retrieval into scores it did not earn. The
/// files the committed cases point at are fixed and few; the feature count decides how many
/// plausible wrong answers surround them.
/// </remarks>
public static class SyntheticFrontendRepository
{
    /// <summary>
    /// Feature names, fixed so that a generated repository is byte-identical between runs.
    /// </summary>
    private static readonly string[] Features =
    [
        "invoice", "shipment", "customer", "warehouse", "payment", "refund", "catalog",
        "pricing", "discount", "inventory", "supplier", "contract", "delivery", "route",
        "vehicle", "driver", "terminal", "receipt", "settlement", "reconciliation",
        "subscription", "renewal", "notification", "template", "audit", "permission",
        "session", "device", "firmware", "telemetry", "incident", "maintenance",
        "schedule", "shift", "payroll", "expense", "budget", "forecast", "report", "export",
    ];

    /// <summary>
    /// Writes the repository and returns the number of files it contains.
    /// </summary>
    /// <param name="root">Directory to write into. Created if missing.</param>
    /// <param name="featureCount">
    /// How many near-neighbour features surround the files the cases ask about. Anything from
    /// zero upwards produces a valid corpus; the committed cases point only at the fixed files,
    /// so raising this makes retrieval harder without invalidating a single case.
    /// </param>
    public static int Write(string root, int featureCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentOutOfRangeException.ThrowIfNegative(featureCount);

        var written = 0;
        foreach (var (path, text) in Files(featureCount))
        {
            var full = Path.Combine(root, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            // "\n" and no BOM, because the line numbers the cases name have to be the line
            // numbers the chunker sees, on every platform.
            File.WriteAllText(full, text.ReplaceLineEndings("\n"), new UTF8Encoding(false));
            written++;
        }

        return written;
    }

    /// <summary>
    /// Every file of the repository, as path and content.
    /// </summary>
    public static IEnumerable<(string Path, string Text)> Files(int featureCount)
    {
        yield return ("tsconfig.json", TsConfig);
        yield return ("types/react.d.ts", ReactTypes);
        foreach (var file in Fixed)
        {
            yield return file;
        }

        for (var index = 0; index < featureCount; index++)
        {
            var name = FeatureName(index);
            var type = Pascal(name);
            yield return ($"src/features/{name}/api/{name}.api.ts", FeatureApi(name, type));
            yield return ($"src/features/{name}/model/use{type}.ts", FeatureHook(name, type));
            yield return ($"src/features/{name}/ui/{type}Card.tsx", FeatureCard(name, type));
            yield return ($"src/features/{name}/lib/format{type}.ts", FeatureFormat(name, type));
            yield return ($"src/features/{name}/model/{name}.types.ts", FeatureTypes(type));
        }
    }

    private static string FeatureName(int index) =>
        index < Features.Length
            ? Features[index]
            : Features[index % Features.Length] +
              (index / Features.Length + 1).ToString(CultureInfo.InvariantCulture);

    private static string Pascal(string name) =>
        char.ToUpperInvariant(name[0]) + name[1..];

    private const string TsConfig = """
        {
          "compilerOptions": {
            "target": "ES2022",
            "module": "ESNext",
            "moduleResolution": "bundler",
            "jsx": "react-jsx",
            "strict": true,
            "skipLibCheck": true
          },
          "include": ["src/**/*.ts", "src/**/*.tsx", "types/**/*.d.ts"]
        }
        """;

    /// <summary>
    /// Enough of React to type-check against, so the repository needs no `npm install` to be
    /// indexed. A declaration file rather than a dependency keeps the corpus a corpus: reproducing
    /// a measurement must not depend on what a registry served on the day.
    /// </summary>
    private const string ReactTypes = """
        declare module 'react' {
          export type ReactNode = unknown;
          export function memo<T>(component: T): T & { displayName?: string };
          export function useMemo<T>(factory: () => T, deps: unknown[]): T;
          export function useState<T>(initial: T): [T, (next: T) => void];
        }
        """;

    /// <summary>
    /// The files the committed cases ask about. Every one of them is a shape the chunker has to
    /// handle differently from its neighbours, and every one is written once so its line numbers
    /// are stable no matter how many features surround it.
    /// </summary>
    private static IEnumerable<(string Path, string Text)> Fixed
    {
        get
        {
            yield return ("src/shared/auth/tokenStorage.ts", """
                import { readCookie, writeCookie } from './cookies';

                const STORAGE_KEY = 'session-token';

                /**
                 * Keeps the access token out of localStorage on purpose: anything that runs in the
                 * page can read it there, and a token that survives a closed tab survives a shared
                 * machine too. The cookie is host-only and expires with the session.
                 */
                export function storeAccessToken(token: string, lifetimeSeconds: number): void {
                  writeCookie(STORAGE_KEY, token, lifetimeSeconds);
                }

                export function readAccessToken(): string | null {
                  return readCookie(STORAGE_KEY);
                }

                export function clearAccessToken(): void {
                  writeCookie(STORAGE_KEY, '', 0);
                }
                """);

            yield return ("src/shared/auth/cookies.ts", """
                export function writeCookie(name: string, value: string, seconds: number): void {
                  const expiry = new Date(Date.now() + seconds * 1000).toUTCString();
                  document.cookie = `${name}=${encodeURIComponent(value)}; expires=${expiry}; path=/`;
                }

                export function readCookie(name: string): string | null {
                  const prefix = `${name}=`;
                  const found = document.cookie.split('; ').find((part) => part.startsWith(prefix));
                  return found ? decodeURIComponent(found.slice(prefix.length)) : null;
                }
                """);

            yield return ("src/shared/api/retryPolicy.ts", """
                const RETRYABLE_STATUS = [502, 503, 504];

                /**
                 * A request is retried only when repeating it cannot change anything but the
                 * outcome: the server never saw it, or said it was busy. A 400 is not retried
                 * because the second attempt is wrong in exactly the same way as the first.
                 */
                export function shouldRetryRequest(status: number, attempt: number): boolean {
                  if (attempt >= 3) {
                    return false;
                  }

                  return status === 0 || RETRYABLE_STATUS.includes(status);
                }

                export function backoffDelayMilliseconds(attempt: number): number {
                  return Math.min(8000, 250 * 2 ** attempt);
                }
                """);

            yield return ("src/shared/access/menuVisibility.ts", """
                import type { MenuNode, Permission } from './types';

                /**
                 * A section is visible when at least one item under it is. Sections carry no
                 * permissions of their own, because a section nobody can enter is a section that
                 * should not be drawn, and duplicating the rule onto the parent is how the two
                 * halves drift apart.
                 */
                export function visibleMenu(nodes: MenuNode[], granted: Permission[]): MenuNode[] {
                  return nodes
                    .map((node) => ({ ...node, children: visibleMenu(node.children ?? [], granted) }))
                    .filter((node) => (node.children.length > 0) || isGranted(node, granted));
                }

                function isGranted(node: MenuNode, granted: Permission[]): boolean {
                  return node.permission === undefined || granted.includes(node.permission);
                }
                """);

            yield return ("src/shared/access/types.ts", """
                export type Permission = string;

                export interface MenuNode {
                  key: string;
                  label: string;
                  permission?: Permission;
                  children?: MenuNode[];
                }
                """);

            yield return ("src/shared/ui/EmptyState.tsx", """
                import { memo } from 'react';

                interface EmptyStateProps {
                  title: string;
                  hint?: string;
                }

                /**
                 * What a list renders instead of nothing at all. Memoised because a table redraws
                 * it on every keystroke of a filter that matches no rows.
                 */
                export const EmptyState = memo(({ title, hint }: EmptyStateProps) => {
                  const description = hint ?? 'Try widening the filter.';

                  return (
                    <div className="empty-state">
                      <h3>{title}</h3>
                      <p>{description}</p>
                    </div>
                  );
                });

                EmptyState.displayName = 'EmptyState';
                """);

            yield return ("src/shared/api/request.ts", """
                const BASE_URL = '/api/v1';

                export async function request<T>(path: string, payload?: unknown): Promise<T> {
                  const response = await fetch(`${BASE_URL}${path}`, {
                    method: payload === undefined ? 'GET' : 'POST',
                    body: payload === undefined ? undefined : JSON.stringify(payload),
                  });

                  return (await response.json()) as T;
                }
                """);

            yield return ("src/shared/lib/money.py", """"
                """Money arithmetic that refuses to be done in floating point."""

                from decimal import Decimal, ROUND_HALF_UP

                CENTS = Decimal("0.01")


                def to_minor_units(amount: Decimal) -> int:
                    """Rounds half up, the way an invoice does, and returns whole cents."""
                    return int(amount.quantize(CENTS, rounding=ROUND_HALF_UP) * 100)


                class MoneyLedger:
                    """Adds amounts without ever leaving exact arithmetic."""

                    def __init__(self) -> None:
                        self.entries: list[Decimal] = []

                    def add(self, amount: Decimal) -> None:
                        self.entries.append(amount)

                    def total(self) -> Decimal:
                        return sum(self.entries, Decimal("0"))
                """");
        }
    }

    private static string FeatureApi(string name, string type) => $$"""
        import { request } from '../../../shared/api/request';
        import type { {{type}} } from '../model/{{name}}.types';

        export const {{name}}Api = {
          list: (page: number) => request<{{type}}[]>(`/{{name}}s?page=${page}`),
          byId: (id: string) => request<{{type}}>(`/{{name}}s/${id}`),
          create: (payload: Partial<{{type}}>) => request<{{type}}>(`/{{name}}s`, payload),
          update: (id: string, payload: Partial<{{type}}>) =>
            request<{{type}}>(`/{{name}}s/${id}`, payload),
          remove: (id: string) => request<void>(`/{{name}}s/${id}`),
        };
        """;

    private static string FeatureHook(string name, string type) => $$"""
        import { useMemo, useState } from 'react';

        import { {{name}}Api } from '../api/{{name}}.api';
        import type { {{type}} } from './{{name}}.types';

        export function use{{type}}List(page: number) {
          const [items, setItems] = useState<{{type}}[]>([]);
          const reload = () => {{name}}Api.list(page).then(setItems);
          const sorted = useMemo(() => [...items].sort(byName), [items]);

          return { items: sorted, reload };
        }

        function byName(left: {{type}}, right: {{type}}): number {
          return left.name.localeCompare(right.name);
        }
        """;

    private static string FeatureCard(string name, string type) => $$"""
        import { memo } from 'react';

        import type { {{type}} } from '../model/{{name}}.types';

        interface {{type}}CardProps {
          value: {{type}};
          onOpen: (id: string) => void;
        }

        export const {{type}}Card = memo(({ value, onOpen }: {{type}}CardProps) => {
          const handleOpen = () => onOpen(value.id);
          const subtitle = value.archived ? 'archived' : 'active';

          return (
            <article className="card" onClick={handleOpen}>
              <h4>{value.name}</h4>
              <span>{subtitle}</span>
            </article>
          );
        });

        {{type}}Card.displayName = '{{type}}Card';
        """;

    private static string FeatureFormat(string name, string type) => $$"""
        import type { {{type}} } from '../model/{{name}}.types';

        const DASH = '—';

        export function format{{type}}(value: {{type}} | null): string {
          if (value === null) {
            return DASH;
          }

          return `${value.name} (${value.id})`;
        }
        """;

    private static string FeatureTypes(string type) => $$"""
        export interface {{type}} {
          id: string;
          name: string;
          archived: boolean;
        }

        export type {{type}}Filter = Partial<Pick<{{type}}, 'name' | 'archived'>>;
        """;
}
