export abstract class Repository<T> {
  abstract load(id: number): Promise<T>;

  protected log(message: string): void {
    console.log(message);
  }
}
