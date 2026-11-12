// The few @angular/core declarations the fixture uses, so the checker resolves inject() as it would in a real
// workspace without installing Angular.
export declare function Component(options: { selector?: string; template?: string; templateUrl?: string; imports?: unknown[] }): ClassDecorator;
export declare function Injectable(options?: { providedIn?: 'root' | null }): ClassDecorator;
export declare function Pipe(options: { name: string }): ClassDecorator;
export declare function inject<T>(token: abstract new (...args: never[]) => T): T;
