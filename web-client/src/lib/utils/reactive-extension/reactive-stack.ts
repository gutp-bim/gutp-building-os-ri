import { Stack } from "../collections";
import { Disposable } from "@/lib/utils/reactive-extension/disposable";
import {
  IReadOnlyReactivePropertyInstance,
  ReactivePropertyInstance,
} from "@/lib/utils/reactive-extension/reactive-property";
import { useEffect, useRef, useState } from "react";

export class ReactiveStack<T> extends Disposable {
  private pushDispatches: Set<() => void> = new Set<() => void>();
  private popDispatches: Set<() => void> = new Set<() => void>();
  private clearDispatches: Set<() => void> = new Set<() => void>();
  private stack: Stack<T> = Stack.create<T>();

  [key: number]: T;

  private constructor(initialValue: T[]) {
    super(() => {
      this.pushDispatches.clear();
      this.popDispatches.clear();
      this.clearDispatches.clear();
    });
    initialValue.forEach((x) => this.stack.push(x));
  }

  static create<T>(initialValue: T[]) {
    const instance = new ReactiveStack<T>(initialValue);
    return new Proxy(instance, {
      get(target: ReactiveStack<T>, prop: string | symbol) {
        if (typeof prop === "string" && !isNaN(Number(prop))) {
          const index = Number(prop);
          return target.stack[index];
        }
        return (target as never)[prop]; // 元のプロパティを返す
      },
    });
  }

  // 要素をスタックに追加
  public push(item: T): void {
    this.stack.push(item);
    this.pushDispatches.forEach((x) => x());
  }

  // スタックから要素を削除し、削除した要素を返す
  public pop(): T | undefined {
    const item = this.stack.pop();
    this.popDispatches.forEach((x) => x());
    return item;
  }

  // スタックの先頭を確認する
  public get peek(): T | undefined {
    return this.stack.peek;
  }

  // スタックが空かどうかを確認
  public get isEmpty(): boolean {
    return this.stack.isEmpty;
  }

  // スタックのサイズを取得
  public get size(): number {
    return this.stack.size;
  }

  // スタックをクリア
  public clear(): void {
    this.stack.clear();
    this.clearDispatches.forEach((x) => x());
  }

  public watch = () => {
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const [_, render] = useState<object>();
    useEffect(() => {
      const d = () => render({});
      this.pushDispatches.add(d);
      this.popDispatches.add(d);
      this.clearDispatches.add(d);
      return () => {
        this.pushDispatches.delete(d);
        this.popDispatches.delete(d);
        this.clearDispatches.delete(d);
      };
    }, []);
    return this.stack;
  };

  public select = <TResult>(
    func: (value: Stack<T>) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> => {
    const output = new ReactivePropertyInstance<TResult>(func(this.stack));

    const d = () => output.setValue(func(this.stack));
    this.pushDispatches.add(d);
    this.popDispatches.add(d);
    this.clearDispatches.add(d);

    this.derivativeSubjects.push(output);
    return output;
  };

  toJSON() {
    return this.stack.toJSON();
  }
}

export const useReactiveStack = <T>(initialValue: T[]): ReactiveStack<T> =>
  useRef<ReactiveStack<T>>(ReactiveStack.create<T>(initialValue)).current;
