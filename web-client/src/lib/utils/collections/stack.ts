export class Stack<T> {
  private items: T[] = [];

  [key: number]: T;

  private constructor() {}

  static create<T>() {
    const instance = new Stack<T>();
    return new Proxy(instance, {
      get(target: Stack<T>, prop: string | symbol) {
        if (typeof prop === "string" && !isNaN(Number(prop))) {
          const index = Number(prop);
          return target.items[target.items.length - 1 - index];
        }
        return (target as never)[prop]; // 元のプロパティを返す
      },
    });
  }

  // 要素をスタックに追加
  push(item: T): void {
    this.items.push(item);
  }

  // スタックから要素を取り出し、削除する
  pop(): T | undefined {
    return this.items.pop();
  }

  // スタックのトップを確認（削除しない）
  get peek(): T | undefined {
    return this.items[this.items.length - 1];
  }

  // スタックが空かどうかを確認
  get isEmpty(): boolean {
    return this.items.length === 0;
  }

  // スタックのサイズを取得
  get size(): number {
    return this.items.length;
  }

  // スタックをクリア
  clear(): void {
    this.items = [];
  }

  toJSON() {
    return JSON.stringify(this.items);
  }
}
