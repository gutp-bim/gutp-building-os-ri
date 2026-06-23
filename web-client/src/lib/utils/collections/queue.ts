export class Queue<T> {
  private items: T[] = [];

  [key: number]: T;

  private constructor() {}

  static create<T>() {
    const instance = new Queue<T>();
    return new Proxy(instance, {
      get(target: Queue<T>, prop: string | symbol) {
        if (typeof prop === "string" && !isNaN(Number(prop))) {
          const index = Number(prop);
          return target.items[index];
        }
        return (target as never)[prop]; // 元のプロパティを返す
      },
    });
  }
  // 要素をキューに追加
  enqueue(item: T): void {
    this.items.push(item);
  }

  // キューから要素を削除し、削除した要素を返す
  dequeue(): T | undefined {
    return this.items.shift();
  }

  // キューの先頭を確認する
  get peek(): T | undefined {
    return this.items[0];
  }

  // キューが空かどうかを確認
  get isEmpty(): boolean {
    return this.items.length === 0;
  }

  // キューのサイズを取得
  get size(): number {
    return this.items.length;
  }

  // キューをクリア
  clear(): void {
    this.items = [];
  }

  toJSON() {
    return JSON.stringify(this.items);
  }
}
