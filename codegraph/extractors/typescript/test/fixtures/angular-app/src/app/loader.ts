export interface Loader {
  load(id: number): Promise<unknown>;
}
