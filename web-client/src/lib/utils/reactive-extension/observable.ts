import { IDisposable } from "@/lib/utils/reactive-extension/disposable";
import { IReadOnlyReactiveProperty } from "@/lib/utils/reactive-extension/reactive-property";
import { Subject } from "@/lib/utils/reactive-extension/subject";

export type IObservable<T> = IDisposable & {
  subscribe: (listener: (value: T) => void) => IDisposable;
  select: <TResult>(selector: (value: T) => TResult) => IObservable<TResult>;
  where: (func: (value: T) => boolean) => IObservable<T>;
  delay: (milliseconds: number) => IObservable<T>;
  combineLatest: <U, TResult>(
    observable: IObservable<U>,
    combiner: (a: T, b: U) => TResult,
  ) => IObservable<TResult>;
  toReadOnlyReactiveProperty: (initialValue: T) => IReadOnlyReactiveProperty<T>;
  registerDerivative: (disposable: IDisposable) => void;
};

export class Observable {
  public static combineLatest = <TLeft, TRight, TResult>(
    left: IObservable<TLeft>,
    right: IObservable<TRight>,
    combiner: (left: TLeft, right: TRight) => TResult,
  ): IObservable<TResult> => {
    const subject = new Subject<TResult>();
    let leftValue: TLeft | undefined = undefined;
    let rightValue: TRight | undefined = undefined;
    const disposables: IDisposable[] = [];

    left
      .subscribe((x) => {
        leftValue = x;
        if (rightValue) subject.onNext(combiner(leftValue, rightValue));
      })
      .addTo(disposables);
    right
      .subscribe((x) => {
        rightValue = x;
        if (leftValue) subject.onNext(combiner(leftValue, rightValue));
      })
      .addTo(disposables);

    left.registerDerivative(subject);
    right.registerDerivative(subject);

    return subject;
  };
}
