import { Queue } from "../collections";
import { Disposable } from "@/lib/utils/reactive-extension/disposable";
import {
  IReadOnlyReactivePropertyInstance,
  ReactivePropertyInstance,
} from "@/lib/utils/reactive-extension/reactive-property";
import { useEffect, useRef, useState } from "react";

export class ReactiveQueue<T> extends Disposable {
  private enqueueDispatches: Set<() => void> = new Set<() => void>();
  private dequeueDispatches: Set<() => void> = new Set<() => void>();
  private clearDispatches: Set<() => void> = new Set<() => void>();
  private queue: Queue<T> = Queue.create<T>();

  [key: number]: T;

  private constructor(initialValue: T[]) {
    super(() => {
      this.enqueueDispatches.clear();
      this.dequeueDispatches.clear();
      this.clearDispatches.clear();
    });
    initialValue.forEach((x) => this.queue.enqueue(x));
  }

  static create<T>(initialValue: T[]) {
    const instance = new ReactiveQueue<T>(initialValue);
    return new Proxy(instance, {
      get(target: ReactiveQueue<T>, prop: string | symbol) {
        if (typeof prop === "string" && !isNaN(Number(prop))) {
          const index = Number(prop);
          return target.queue[index];
        }
        return (target as never)[prop]; // 元のプロパティを返す
      },
    });
  }

  // 要素をキューに追加
  public enqueue(item: T): void {
    this.queue.enqueue(item);
    this.enqueueDispatches.forEach((x) => x());
  }

  // キューから要素を削除し、削除した要素を返す
  public dequeue(): T | undefined {
    const item = this.queue.dequeue();
    this.dequeueDispatches.forEach((x) => x());
    return item;
  }

  // キューの先頭を確認する
  public get peek(): T | undefined {
    return this.queue.peek;
  }

  // キューが空かどうかを確認
  public get isEmpty(): boolean {
    return this.queue.isEmpty;
  }

  // キューのサイズを取得
  public get size(): number {
    return this.queue.size;
  }

  // キューをクリア
  public clear(): void {
    this.queue.clear();
    this.clearDispatches.forEach((x) => x());
  }

  public watch = () => {
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const [_, render] = useState<object>();
    useEffect(() => {
      const d = () => render({});
      this.enqueueDispatches.add(d);
      this.dequeueDispatches.add(d);
      this.clearDispatches.add(d);
      return () => {
        this.enqueueDispatches.delete(d);
        this.dequeueDispatches.delete(d);
        this.clearDispatches.delete(d);
      };
    }, []);
    return this.queue;
  };

  public select = <TResult>(
    func: (value: Queue<T>) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> => {
    const output = new ReactivePropertyInstance<TResult>(func(this.queue));

    const d = () => output.setValue(func(this.queue));
    this.enqueueDispatches.add(d);
    this.dequeueDispatches.add(d);
    this.clearDispatches.add(d);

    this.derivativeSubjects.push(output);
    return output;
  };

  toJSON() {
    return this.queue.toJSON();
  }
}

export const useReactiveQueue = <T>(initialValue: T[]): ReactiveQueue<T> =>
  useRef<ReactiveQueue<T>>(ReactiveQueue.create<T>(initialValue)).current;
