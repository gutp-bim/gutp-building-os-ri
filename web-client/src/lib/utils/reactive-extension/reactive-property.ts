"use client";
import { Disposable, IDisposable } from "@/lib/utils/reactive-extension/disposable";
import { IObservable } from "@/lib/utils/reactive-extension/observable";
import { Subject } from "@/lib/utils/reactive-extension/subject";
import { Dispatch, SetStateAction, useEffect, useRef, useState } from "react";
import { delay } from "./delay";

export type IReadOnlyReactiveProperty<T> = {
  value: T;
  watch: () => T;
  subscribe: (func: (value: T) => void) => IDisposable;
  where: (func: (value: T) => boolean) => IObservable<T>;
  select: <TResult>(
    func: (value: T) => TResult,
  ) => IReadOnlyReactivePropertyInstance<TResult>;
  combineLatest: <TProperty, TResult>(
    property: IReadOnlyReactivePropertyInstance<TProperty>,
    func: (a: T, b: TProperty) => TResult,
  ) => IReadOnlyReactivePropertyInstance<TResult>;
  pairwise: () => IReadOnlyReactiveProperty<{ prev: T | undefined; curr: T }>;
  delay: (milliseconds: number) => IReadOnlyReactiveProperty<T>;
  getInstance: () => IReadOnlyReactivePropertyInstance<T>;
};

export type IReactiveProperty<T> = IReadOnlyReactiveProperty<T> & {
  setValue: Dispatch<SetStateAction<T>>;
  toReadOnlyReactiveProperty: () => IReadOnlyReactiveProperty<T>;
};

export type IReadOnlyReactivePropertyInstance<T> =
  IReadOnlyReactiveProperty<T> & {
    /**
     * 本来であれば internal にしたい
     * @param dispatch
     */
    registerDispatch: (dispatch: () => void) => void;

    /**
     * 本来であれば internal にしたい
     * @param dispatch
     */
    unregisterDispatch: (dispatch: () => void) => void;
  };

export class ReactivePropertyInstance<T>
  extends Disposable
  implements IReactiveProperty<T>, IReadOnlyReactivePropertyInstance<T>
{
  private dispatches: Set<() => void> = new Set<() => void>();
  public value: T;

  public constructor(initialValue: T) {
    super(() => {
      this.dispatches.clear();
    });
    this.value = initialValue;
  }

  public getInstance = (): IReadOnlyReactivePropertyInstance<T> => this;
  public toReadOnlyReactiveProperty = (): IReadOnlyReactiveProperty<T> => this;
  public toReactiveProperty = (): IReactiveProperty<T> => this;

  public registerDispatch = (dispatch: () => void) => {
    this.dispatches.add(dispatch);
  };

  public unregisterDispatch = (dispatch: () => void) => {
    this.dispatches.delete(dispatch);
  };

  public setValue: Dispatch<SetStateAction<T>> = (value: SetStateAction<T>) => {
    if (typeof value === "function") {
      this.value = (value as (prev: T) => T)(this.value);
    } else {
      this.value = value;
    }
    this.dispatches.forEach((x) => x());
  };

  public watch = () => {
    // eslint-disable-next-line @typescript-eslint/no-unused-vars
    const [_, render] = useState<object>();
    useEffect(() => {
      const d = () => render({});
      this.registerDispatch(d);
      return () => {
        this.unregisterDispatch(d);
      };
    }, []);
    return this.value;
  };

  public subscribe = (func: (value: T) => void) => {
    const d = () => func(this.value);
    this.registerDispatch(d);
    return new Disposable(() => this.unregisterDispatch(d));
  };

  public select = <TResult>(
    func: (value: T) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> => {
    const output = new ReactivePropertyInstance<TResult>(func(this.value));

    const d = () => output.setValue(func(this.value));
    this.registerDispatch(d);

    this.derivativeSubjects.push(output);
    return output;
  };

  public where = (func: (value: T) => boolean): IObservable<T> => {
    const output = new Subject<T>();

    const d = () => {
      if (func(this.value)) output.onNext(this.value);
    };
    this.registerDispatch(d);

    this.derivativeSubjects.push(output);
    return output;
  };

  public combineLatest = <TProperty, TResult>(
    property: IReadOnlyReactivePropertyInstance<TProperty>,
    func: (a: T, b: TProperty) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> =>
    ReactivePropertyInstance.combineLatest(this, property, func);

  public pairwise = () => {
    const output = new ReactivePropertyInstance<{
      prev: T | undefined;
      curr: T;
    }>({ prev: this.value, curr: this.value });

    const d = () => {
      output.setValue({ prev: output.value.curr, curr: this.value });
    };
    this.registerDispatch(d);
    this.derivativeSubjects.push(output);

    return output;
  };

  public delay = (milliseconds: number) => {
    const output = new ReactivePropertyInstance<T>(this.value);
    const d = () => {
      const waitAndSet = async () => {
        await delay(milliseconds);
        output.setValue(this.value);
      };
      waitAndSet();
    };

    this.registerDispatch(d);
    this.derivativeSubjects.push(output);
    return output;
  };

  public static combineLatest = <TPropertyLeft, TPropertyRight, TResult>(
    left: IReadOnlyReactivePropertyInstance<TPropertyLeft>,
    right: IReadOnlyReactivePropertyInstance<TPropertyRight>,
    func: (left: TPropertyLeft, right: TPropertyRight) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> => {
    const output = new ReactivePropertyInstance<TResult>(
      func(left.value, right.value),
    );

    const d = () => {
      const result = func(left.value, right.value);
      output.setValue(result);
    };
    left.registerDispatch(d);
    right.registerDispatch(d);

    return output;
  };

  public static combineArrayLatest = <TResult>(
    properties: IReadOnlyReactivePropertyInstance<never>[],
    func: (properties: never[]) => TResult,
  ): IReadOnlyReactivePropertyInstance<TResult> => {
    const output = new ReactivePropertyInstance<TResult>(
      func(properties.map((x) => x.value)),
    );

    const d = () => {
      const result = func(properties.map((x) => x.value));
      output.setValue(result);
    };
    properties.forEach((x) => x.registerDispatch(d));

    return output;
  };
}

/**
 * 状態の更新時、状態の更新のみを行う Component は再レンダリングされず、
 * Subscribe した Component のみが再レンダリングされる状態管理 hooks
 * 主に、親 Component が状態の更新を担当し、子 Component が状態を使用するケース (ContextAPI等) にて、
 * 親の無駄なレンダリングを抑えたい場合に使用することを想定している
 * @param initialValue
 */
export const useReactiveProperty = <T>(initialValue: T): IReactiveProperty<T> =>
  useRef<IReactiveProperty<T>>(new ReactivePropertyInstance<T>(initialValue))
    .current;
