using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace CourierModule
{
    public enum OrderState { Pending, InTransit, Completed }

    public enum OpResult
    {
        Success,
        DuplicateOrder,
        OrderNotFound,
        TransportNotFound,
        OrderAlreadyAssigned,
        TransportBusy,
        TransportCannotTake,
        NotAssigned,
        PaymentDeclined
    }

    public static class ResultText
    {
        public static string Describe(this OpResult r)
        {
            switch (r)
            {
                case OpResult.Success: return "виконано";
                case OpResult.DuplicateOrder: return "замовлення з таким номером уже існує";
                case OpResult.OrderNotFound: return "замовлення не знайдено серед активних";
                case OpResult.TransportNotFound: return "такого транспорту немає у службі";
                case OpResult.OrderAlreadyAssigned: return "замовлення вже призначене на транспорт";
                case OpResult.TransportBusy: return "транспорт зайнятий";
                case OpResult.TransportCannotTake: return "транспорт не може взяти це замовлення";
                case OpResult.NotAssigned: return "замовлення ще не призначене на транспорт";
                case OpResult.PaymentDeclined: return "оплату відхилено, доставка не завершена";
                default: return r.ToString();
            }
        }
    }

    public interface IPaymentMethod
    {
        string Title { get; }
        bool TryCharge(decimal amount, out string message);
    }

    public sealed class CardPayment : IPaymentMethod
    {
        private readonly string _holder;
        private decimal _balance;

        public string Title => "Картка";

        public CardPayment(string holder, decimal balance)
        {
            _holder = holder;
            _balance = balance;
        }

        public void TopUp(decimal sum) => _balance += sum;

        public bool TryCharge(decimal amount, out string message)
        {
            if (_balance < amount)
            {
                message = $"на картці {_holder} лише {_balance:F2} грн, потрібно {amount:F2} грн";
                return false;
            }
            _balance -= amount;
            message = $"з картки {_holder} списано {amount:F2} грн (залишок {_balance:F2} грн)";
            return true;
        }
    }

    public sealed class CashPayment : IPaymentMethod
    {
        private readonly decimal _handed;

        public string Title => "Готівка";

        public CashPayment(decimal handed) => _handed = handed;

        public bool TryCharge(decimal amount, out string message)
        {
            if (_handed < amount)
            {
                message = $"клієнт дав {_handed:F2} грн, а потрібно {amount:F2} грн";
                return false;
            }
            message = $"прийнято {_handed:F2} грн, решта {_handed - amount:F2} грн";
            return true;
        }
    }

    public sealed class Order
    {
        public int Number { get; }
        public string Description { get; }
        public double Weight { get; }
        public double Distance { get; }
        public OrderState State { get; internal set; } = OrderState.Pending;

        public Order(int number, string description, double weight, double distance)
        {
            if (weight <= 0) throw new ArgumentOutOfRangeException(nameof(weight), "вага має бути більшою за 0");
            if (distance <= 0) throw new ArgumentOutOfRangeException(nameof(distance), "відстань має бути більшою за 0");
            Number = number;
            Description = description;
            Weight = weight;
            Distance = distance;
        }

        public override string ToString() =>
            $"№{Number} «{Description}» ({Weight} кг, {Distance} км)";
    }


    public abstract class Transport
    {
        public int Id { get; }
        public string Kind { get; }
        public double WeightLimit { get; }
        public bool IsBusy { get; private set; }

        protected Transport(int id, string kind, double weightLimit)
        {
            Id = id;
            Kind = kind;
            WeightLimit = weightLimit;
        }

        public abstract decimal CalculateCost(Order order);

        public virtual bool CanTake(Order order) => order.Weight <= WeightLimit;

        internal void StartJob() => IsBusy = true;
        internal void FinishJob() => IsBusy = false;

        public override string ToString() =>
            $"{Kind} #{Id} [{(IsBusy ? "зайнятий" : "вільний")}, до {WeightLimit} кг]";
    }

    public sealed class Bike : Transport
    {
        private const decimal Base = 20m, PerKm = 5m;
        private const double RangeLimit = 10.0;

        public Bike(int id, double weightLimit = 8) : base(id, "Велосипед", weightLimit) { }

        public override decimal CalculateCost(Order o) => Base + PerKm * (decimal)o.Distance;

        public override bool CanTake(Order o) => base.CanTake(o) && o.Distance <= RangeLimit;
    }

    public sealed class Car : Transport
    {
        private const decimal Base = 50m, PerKm = 8m, PerKg = 1m;

        public Car(int id, double weightLimit = 400) : base(id, "Автомобіль", weightLimit) { }

        public override decimal CalculateCost(Order o) =>
            Base + PerKm * (decimal)o.Distance + PerKg * (decimal)o.Weight;
    }

    public sealed class Truck : Transport
    {
        private const decimal Base = 200m, PerKm = 15m, PerKg = 0.5m;

        public Truck(int id, double weightLimit = 8000) : base(id, "Вантажівка", weightLimit) { }

        public override decimal CalculateCost(Order o) =>
            Base + PerKm * (decimal)o.Distance + PerKg * (decimal)o.Weight;
    }

    internal sealed class Assignment
    {
        public Transport Transport { get; }
        public decimal Price { get; }

        public Assignment(Transport transport, decimal price)
        {
            Transport = transport;
            Price = price;
        }
    }

    public sealed class HistoryEntry
    {
        public Order Order { get; }
        public Transport Transport { get; }
        public decimal Paid { get; }
        public string Method { get; }

        public HistoryEntry(Order order, Transport transport, decimal paid, string method)
        {
            Order = order;
            Transport = transport;
            Paid = paid;
            Method = method;
        }
    }
    public sealed class DeliveryService
    {
        private readonly List<Transport> _fleet = new List<Transport>();
        private readonly Dictionary<int, Order> _active = new Dictionary<int, Order>();
        private readonly Dictionary<int, Assignment> _assignments = new Dictionary<int, Assignment>();
        private readonly List<HistoryEntry> _archive = new List<HistoryEntry>();
        private readonly HashSet<int> _usedNumbers = new HashSet<int>();

        public void RegisterTransport(Transport t)
        {
            _fleet.Add(t);
        }

        public Order CreateOrder(int number, string description, double weight, double distance)
        {
            if (!_usedNumbers.Add(number))
                throw new InvalidOperationException($"замовлення №{number} вже існувало");

            try
            {
                var order = new Order(number, description, weight, distance);
                _active.Add(number, order);
                return order;
            }
            catch
            {
                _usedNumbers.Remove(number);
                throw;
            }
        }

        public List<(Transport Transport, decimal Cost)> GetQuotes(Order order) =>
            _fleet.Where(t => t.CanTake(order))
                  .Select(t => (t, t.CalculateCost(order)))
                  .ToList();

        public void PrintQuotes(Order order)
        {
            Console.WriteLine($"Варіанти доставки для {order}:");
            var quotes = GetQuotes(order);
            if (quotes.Count == 0)
            {
                Console.WriteLine("   — немає транспорту, який може його взяти");
                return;
            }
            foreach (var q in quotes)
                Console.WriteLine($"   {q.Transport} — {q.Cost:F2} грн");
        }

        public OpResult Assign(Order order, Transport transport)
        {
            if (!_active.TryGetValue(order.Number, out var known) || !ReferenceEquals(known, order))
                return OpResult.OrderNotFound;
            if (!_fleet.Contains(transport))
                return OpResult.TransportNotFound;
            if (_assignments.ContainsKey(order.Number))
                return OpResult.OrderAlreadyAssigned;
            if (transport.IsBusy)
                return OpResult.TransportBusy;
            if (!transport.CanTake(order))
                return OpResult.TransportCannotTake;

            transport.StartJob();
            order.State = OrderState.InTransit;
            _assignments[order.Number] = new Assignment(transport, transport.CalculateCost(order));
            return OpResult.Success;
        }

        public OpResult Deliver(Order order, IPaymentMethod method)
        {
            if (!_active.ContainsKey(order.Number))
                return OpResult.OrderNotFound;
            if (!_assignments.TryGetValue(order.Number, out var job))
                return OpResult.NotAssigned;

            bool paid = method.TryCharge(job.Price, out string message);
            Console.WriteLine($"   Оплата ({method.Title}): {message}");
            if (!paid)
                return OpResult.PaymentDeclined;

            order.State = OrderState.Completed;
            job.Transport.FinishJob();
            _assignments.Remove(order.Number);
            _active.Remove(order.Number);
            _archive.Add(new HistoryEntry(order, job.Transport, job.Price, method.Title));
            return OpResult.Success;
        }

        public void PrintFleet()
        {
            Console.WriteLine("Парк транспорту:");
            foreach (var t in _fleet)
                Console.WriteLine($"   {t}");
        }

        public void PrintActive()
        {
            Console.WriteLine("Активні замовлення:");
            if (_active.Count == 0)
            {
                Console.WriteLine("   — немає");
                return;
            }
            foreach (var o in _active.Values.OrderBy(x => x.Number))
            {
                string info = _assignments.TryGetValue(o.Number, out var a)
                    ? $"їде на {a.Transport.Kind} #{a.Transport.Id}, до сплати {a.Price:F2} грн"
                    : "чекає на призначення";
                Console.WriteLine($"   {o} — {info}");
            }
        }

        public void PrintHistory()
        {
            Console.WriteLine("Історія доставок:");
            if (_archive.Count == 0)
            {
                Console.WriteLine("   — порожньо");
                return;
            }
            foreach (var h in _archive)
                Console.WriteLine($"   {h.Order} — {h.Transport.Kind} #{h.Transport.Id}, {h.Paid:F2} грн, {h.Method}");
        }
    }


    internal static class Program
    {
        private static void Section(string title) =>
            Console.WriteLine($"\n######## {title} ########");

        private static void Show(string action, OpResult result) =>
            Console.WriteLine($"{(result == OpResult.Success ? "[OK]  " : "[ПОМ.]")} {action}: {result.Describe()}");

        private static Order TryCreate(DeliveryService s, int num, string text, double kg, double km)
        {
            try
            {
                var o = s.CreateOrder(num, text, kg, km);
                Console.WriteLine($"[OK]   створено {o}");
                return o;
            }
            catch (Exception ex) when (ex is ArgumentException || ex is InvalidOperationException)
            {
                Console.WriteLine($"[ПОМ.] замовлення №{num} не створено: {ex.Message.Split('\n')[0]}");
                return null;
            }
        }

        private static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            var service = new DeliveryService();

            Section("Транспорт");
            var bikeA = new Bike(1);
            var bikeB = new Bike(2);
            var car = new Car(3);
            var truck = new Truck(4);
            foreach (Transport t in new Transport[] { bikeA, bikeB, car, truck })
                service.RegisterTransport(t);
            service.PrintFleet();

            Section("Створення замовлень");
            var docs = TryCreate(service, 101, "Документи", 0.5, 4);
            var pizza = TryCreate(service, 102, "Піца", 2, 14);
            var fridge = TryCreate(service, 103, "Холодильник", 70, 25);
            var cement = TryCreate(service, 104, "Цемент", 3000, 40);
            var reactor = TryCreate(service, 105, "Реактор", 20000, 5);
            var flowers = TryCreate(service, 106, "Квіти", 1.5, 9.5);
            var edge = TryCreate(service, 107, "Пакунок (рівно 10 км)", 3, 10);
            TryCreate(service, 108, "Нульова вага", 0, 5);
            TryCreate(service, 109, "Від'ємна відстань", 2, -3);
            TryCreate(service, 101, "Дубль номера", 1, 1);

            Section("Вартість доставки кожним транспортом");
            foreach (var o in new[] { docs, pizza, fridge, cement, reactor, edge })
                service.PrintQuotes(o);

            Section("Призначення");
            Show("101 -> велосипед #1", service.Assign(docs, bikeA));
            Show("106 -> велосипед #1 (зайнятий)", service.Assign(flowers, bikeA));
            Show("102 -> велосипед #2 (14 км)", service.Assign(pizza, bikeB));
            Show("103 -> велосипед #2 (70 кг)", service.Assign(fridge, bikeB));
            Show("103 -> автомобіль #3", service.Assign(fridge, car));
            Show("104 -> автомобіль (3000 кг)", service.Assign(cement, car));
            Show("104 -> вантажівка #4", service.Assign(cement, truck));
            Show("101 -> велосипед #2 (уже призначене)", service.Assign(docs, bikeB));
            Show("105 -> вантажівка (зайнята)", service.Assign(reactor, truck));
            Show("замовлення поза службою", service.Assign(new Order(999, "Стороннє", 1, 1), bikeB));
            Show("транспорт поза службою", service.Assign(pizza, new Car(50)));
            Console.WriteLine();
            service.PrintFleet();
            service.PrintActive();

            Section("Доставка та оплата");
            Show("102 без призначення", service.Deliver(pizza, new CashPayment(100)));

            var card = new CardPayment("Олена К.", 50m);
            Show("101, мало коштів на картці", service.Deliver(docs, card));
            card.TopUp(100m);
            Show("101, картка після поповнення", service.Deliver(docs, card));

            Show("103, замало готівки", service.Deliver(fridge, new CashPayment(100)));
            Show("103, готівка з рештою", service.Deliver(fridge, new CashPayment(500)));

            Show("104, картка", service.Deliver(cement, new CardPayment("ТОВ Будінвест", 10000m)));
            Show("101 повторно", service.Deliver(docs, new CashPayment(500)));

            Section("Повторне використання вільного транспорту");
            Show("106 -> велосипед #1", service.Assign(flowers, bikeA));
            Show("107 -> велосипед #2 (рівно 10 км)", service.Assign(edge, bikeB));
            Show("106, готівка", service.Deliver(flowers, new CashPayment(70)));
            Show("107, картка", service.Deliver(edge, new CardPayment("Петро С.", 100m)));

            Section("Підсумок");
            service.PrintFleet();
            service.PrintActive();
            service.PrintHistory();
        }
    }
}