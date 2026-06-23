import {
  Disposable,
  IDisposable,
} from "@/lib/utils/reactive-extension/disposable";
import {
  IObservable,
  Observable,
} from "@/lib/utils/reactive-extension/observable";
import { ReactivePropertyInstance } from "@/lib/utils/reactive-extension/reactive-property";
import { delay } from "./delay";

export class Subject<T> extends Disposable implements IObservable<T> {
  private listeners: ((event: T) => void)[] = [];
  public existListener = () => this.listeners.length > 0;

  public onNext = (value: T) => {
    this.listeners.forEach((x) => x(value));
  };

  public subscribe = (listener: (value: T) => void) => {
    this.listeners.push(listener);
    const disposable = new Disposable(() => {
      this.listeners = this.listeners.filter((x) => x !== x);
    });
    this.derivativeSubjects.push(disposable);
    return disposable;
  };

  public select = <TResult>(
    selector: (value: T) => TResult,
  ): IObservable<TResult> => {
    const subject = new Subject<TResult>();
    subject.addTo(this.derivativeSubjects);
    const func = (event: T) =>
      subject.existListener() && subject.onNext(selector(event));
    this.listeners.push(func);
    return subject;
  };

  public where = (condition: (value: T) => boolean): IObservable<T> => {
    const subject = new Subject<T>();
    subject.addTo(this.derivativeSubjects);
    const func = (event: T) =>
      subject.existListener() && condition(event) && subject.onNext(event);
    this.listeners.push(func);
    return subject;
  };

  public combineLatest = <U, TResult>(
    observable: IObservable<U>,
    combiner: (a: T, b: U) => TResult,
  ): IObservable<TResult> => {
    return Observable.combineLatest<T, U, TResult>(this, observable, combiner);
  };

  public delay = (milliseconds: number) => {
    const subject = new Subject<T>();
    const func = (event: T) => {
      const waitAndSet = async () => {
        await delay(milliseconds);
        subject.onNext(event);
      };
      if (subject.existListener()) waitAndSet();
    };
    this.listeners.push(func);
    return subject;
  };

  public toReadOnlyReactiveProperty = (initialValue: T) => {
    const property = new ReactivePropertyInstance<T>(initialValue);
    this.registerDerivative(property);
    return property;
  };

  public registerDerivative = (disposable: IDisposable) =>
    this.derivativeSubjects.push(disposable);

  public constructor() {
    super(() => {
      this.listeners = [];
      this.derivativeSubjects.forEach((x) => x.dispose());
      this.isDisposed = true;
    });
  }
}
