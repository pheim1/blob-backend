﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Blob_API.Model;
using Microsoft.Extensions.Logging;
using AutoMapper;
using Blob_API.RessourceModels;
using Microsoft.AspNetCore.Authorization;
using OpenIddict.Validation;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.CompilerServices;

namespace Blob_API.Controllers
{
    [Route("api/[controller]")]
    [Authorize(AuthenticationSchemes = OpenIddictValidationDefaults.AuthenticationScheme)]
    [ApiController]
    public class OrderController : ControllerBase
    {
        private readonly BlobContext _context;
        private readonly ILogger<OrderController> _logger;
        private readonly IMapper _mapper;

        public OrderController(BlobContext context, ILogger<OrderController> logger, IMapper mapper)
        {
            _context = context;
            _logger = logger;
            _mapper = mapper;
        }

        /// GET: api/Orders
        [HttpGet]
        [ProducesResponseType(200)]
        public async Task<ActionResult<IEnumerable<OrderRessource>>> GetOrdersAsync()
        {
            var orderList = await _context.Order.ToListAsync();

            IEnumerable<OrderRessource> orderRessourceList = _mapper.Map<IEnumerable<OrderRessource>>(orderList);

            await AddQuantityToOrder(orderRessourceList);

            return Ok(orderRessourceList);
        }

        // GET: api/Orders/5
        [HttpGet("{id}")]
        [ActionName(nameof(GetOrderAsync))]
        [ProducesResponseType(204)]
        [ProducesResponseType(400)]
        [ProducesResponseType(404)]
        public async Task<ActionResult<OrderRessource>> GetOrderAsync(uint id)
        {
            var order = await _context.Order.FindAsync(id);

            if (order == null)
            {
                return NotFound();
            }

            var orderRessource = _mapper.Map<OrderRessource>(order);

            await AddQuantityToOrder(Enumerable.Repeat(orderRessource, 1));

            return Ok(orderRessource);
        }

        // PUT: api/Orders
        [HttpPut]
        [ProducesResponseType(204)]
        [ProducesResponseType(404)]
        [ProducesResponseType(500)]
        public async Task<IActionResult> PutOrderAsync([FromBody] IEnumerable<OrderRessource> orderRessources)
        {
            using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                // Update entries
                foreach (var orderRessource in orderRessources)
                {
                    // check if the order exists.
                    if (!OrderExists(orderRessource.Id))
                    {
                        return NotFound("One or more objects did not exist in the Database, Id was not found.");
                    }

                    // check if the state id exists.
                    if (!(await _context.State.AnyAsync(x => x.Id == orderRessource.State.Id)))
                    {
                        return NotFound($"The State with the ID={orderRessource.State.Id}, was not found in the Database.");
                    }

                    var orderToUpdate = await _context.Order.FindAsync(orderRessource.Id);

                    // delete old relations, flag entity as deleted.
                    var listOfOrderedProduct = _context.OrderedProductOrder.ToList();
                    foreach (var orderedProductOrder in listOfOrderedProduct)
                    {
                        if (orderedProductOrder.OrderId == orderRessource.Id)
                        {
                            _context.Entry(orderedProductOrder).State = EntityState.Deleted;
                        }
                    }

                    // Update state
                    orderToUpdate.State = await _context.State.FindAsync(orderRessource.State.Id);

                    foreach (var orderedProductRessource in orderRessource.OrderedProducts)
                    {
                        OrderedProduct ordProd;

                        // Search the product that is already backup, but what if the orderedProduct is new to the order?
                        if (orderedProductRessource.Name == null && orderedProductRessource.Sku == null)
                            ordProd = _context.OrderedProduct.Where(ordProd => ordProd.Product.Id == orderedProductRessource.Id).FirstOrDefault();
                        else
                            ordProd = _context.OrderedProduct.Where(ordProd => ordProd.Id == orderedProductRessource.Id).FirstOrDefault();

 

                        // check if the product exists.
                        Product product = _context.Product.Find(orderedProductRessource.Id);
                        if (product == null)
                        {
                            // if the product is not backuped, error...
                            if (ordProd == null)
                            {
                                return NotFound($"The ordered product with the ID={orderedProductRessource.Id} was not found.");
                            }
                        }

                        // Check if product is already in OrderedProductOrder table.
                        OrderedProductOrder ordProdOrd = null;
                        if (orderedProductRessource.Name == null && orderedProductRessource.Sku == null)
                        { 
                           if (ordProd!= null) ordProdOrd = _context.OrderedProductOrder.Find(orderedProductRessource.Id, orderRessource.Id)?.OrderedProduct.ProductId == ordProd.ProductId ? _context.OrderedProductOrder.Find(orderedProductRessource.Id, orderRessource.Id) : null;
                        }                        
                        else
                            ordProdOrd = _context.OrderedProductOrder.Find(orderedProductRessource.Id, orderRessource.Id);

                        // If not create it.
                        if (ordProdOrd == null)
                        {

                            #region Backup Product
                            // Add "ghost/copy/backup"-Product if no entry exists.
                            if (product != null) ordProd = _context.OrderedProduct.Where(ordProd => ordProd.ProductId == product.Id).FirstOrDefault();

                            if (ordProd == null)
                            {
                                // TODO: Check values, sanitize.
                                ordProd = new OrderedProduct()
                                {
                                    Name = product.Name,
                                    Price = product.Price,
                                    Sku = product.Sku,
                                    Product = product
                                };
                                await _context.OrderedProduct.AddAsync(ordProd);
                            }                            
                            #endregion

                            ordProdOrd = new OrderedProductOrder()
                            {
                                OrderedProduct = ordProd,
                                Order = orderToUpdate,
                                Quantity = orderedProductRessource.Quantity,
                            };
                            await _context.OrderedProductOrder.AddAsync(ordProdOrd);

                            #region Reduce Stock                       
                            // Reduce the stock of the item on the location with the highest quantity.
                            var productsAtLocation = _context.LocationProduct.OrderByDescending(x => x.Quantity).Where(x => x.ProductId == ordProd.Product.Id).ToList();
                            if (productsAtLocation == null)
                            {
                                return BadRequest($"Not enough quantity for productId: {product.Id}");
                            }

                            // Check if enough products in stock and reduce.
                            uint reduced = ReduceStock(orderedProductRessource, productsAtLocation);

                            // still items to be reduced, but nothing in stock.
                            if (reduced > 0)
                            {
                                return BadRequest($"Not enough quantity for productId: {product.Id}");
                            }
                            #endregion
                        }
                        else if (ordProdOrd.Quantity != orderedProductRessource.Quantity)
                        {
                            int delta = (int)ordProdOrd.Quantity - (int)orderedProductRessource.Quantity;
                            ordProdOrd.Quantity = orderedProductRessource.Quantity;

                            var productsAtLocation = _context.LocationProduct.OrderByDescending(x => x.Quantity).Where(x => x.ProductId == ordProd.Product.Id).ToList();
                            if (productsAtLocation == null)
                            {
                                return BadRequest($"Not enough quantity for productId: {orderedProductRessource.Id}");
                            }

                            // withdraw stock
                            if (delta > 0)
                            {
                                var temp = productsAtLocation[0].Quantity;
                                productsAtLocation[0].Quantity += (uint)delta;

                                if (temp + delta < 0)
                                    return BadRequest($"Not enough quantity for orderedProductId: {orderedProductRessource.Id}");
                            }
                            // reduce stock
                            else if (delta < 0)
                            {                               
                                if (ReduceStock(orderedProductRessource, productsAtLocation, (uint)(delta*-1)) > 0)
                                    return BadRequest($"Not enough quantity for orderedProductId: {orderedProductRessource.Id}");
                            }

                            _context.Entry(ordProdOrd).State = EntityState.Modified;
                        }

                        // Remove the delete flag if product is in order payload.
                        if (_context.Entry(ordProdOrd).State == EntityState.Deleted)
                        {
                            _context.Entry(ordProdOrd).State = EntityState.Modified;                        
                        }
                    }

                    listOfOrderedProduct.ForEach(orderedProduct =>
                    {
                        if (orderedProduct.OrderId == orderRessource.Id && _context.Entry(orderedProduct).State == EntityState.Deleted)
                        {
                            var loactionWithThisProduct = _context.LocationProduct.AsNoTracking().Where(locProd => locProd.ProductId == orderedProduct.OrderedProduct.ProductId).FirstOrDefault();

                            if (loactionWithThisProduct != null)
                            {
                                var loc_prod = _context.LocationProduct.Find(loactionWithThisProduct.LocationId, orderedProduct.OrderedProduct.ProductId);

                                if (loc_prod != null)
                                {
                                    loc_prod.Quantity += orderedProduct.Quantity;
                                } 
                            }   
                        }
                    });
                }

                var result = await TryContextSaveAsync();
                
                if (result != null) return result;

                await transaction.CommitAsync();

                return NoContent();
            }
        }

        // POST: api/Orders
        [HttpPost]
        [ProducesResponseType(201)]
        [ProducesResponseType(500)]
        public async Task<ActionResult<OrderRessource>> PostOrderAsync(OrderRessource orderRessource)
        {
            // TODO: Reduce Stock


            using (var transaction = await _context.Database.BeginTransactionAsync())
            {
                // Check if the customer already exists.
                Customer customer = _context.Customer.Find(orderRessource.Customer.Id);
                if (customer == null)
                {
                    return BadRequest("The customer does not exist.");
                }

                // Check if the address already exists.

                Address address = _context.Address.Find(customer.Address.Id);
                if (address == null)
                {
                    return BadRequest("The address does not exist.");
                }

                #region Backup Address
                var backupedAddress = _context.OrderedAddress.Where(x => x.City == address.City && x.Street == address.Street && x.Zip == address.Zip).FirstOrDefault();

                // Create "ghost/copy/backup"-Address
                if (backupedAddress == null)
                {
                    backupedAddress = _mapper.Map<OrderedAddress>(address);
                    backupedAddress.Id = 0; // Let EF Core set it from database
                    await _context.OrderedAddress.AddAsync(backupedAddress);
                }

                #endregion

                #region Backup Customer
                OrderedCustomer newOrderedCustomer = _context.OrderedCustomer.Where(x => x.Firstname == customer.Firstname && x.Lastname == customer.Lastname && x.OrderedAddressId == backupedAddress.Id).FirstOrDefault();

                // Create "ghost/copy/backup"-Customer
                if (newOrderedCustomer == null)
                {
                    newOrderedCustomer = new OrderedCustomer()
                    {
                        Firstname = customer.Firstname,
                        Lastname = customer.Lastname,
                        OrderedAddress = backupedAddress
                    };
                    await _context.OrderedCustomer.AddAsync(newOrderedCustomer);
                }    
                #endregion

                // Check if state is provided.
                uint stateId = 1;
                if (orderRessource.State != null)
                {
                    stateId = orderRessource.State.Id;
                }

                Order newOrder = new Order
                {
                    CreatedAt = DateTime.Now.ToUniversalTime(),
                    Customer = customer,
                    State = await _context.State.FindAsync(stateId),
                    OrderedCustomer = newOrderedCustomer
                };

                await _context.Order.AddAsync(newOrder);

                OrderedProductRessource[] array = orderRessource.OrderedProducts.ToArray();

                for (int i = 0; i < array.Length; i++)
                {
                    OrderedProductRessource orderedProductRessource = array[i];
                    OrderedProduct orderedProduct = _mapper.Map<OrderedProduct>(orderedProductRessource);

                    // check if the product exists.
                    Product product = _context.Product.Find(orderedProductRessource.Id);
                    if (product == null)
                    {
                        return NotFound($"The ordered product with the ID={orderedProductRessource.Id} was not found.");
                    }

                    #region Backup Product
                    // Add "ghost/copy/backup"-Product if no entry exists.
                    uint orderedProductId = 0;
                    OrderedProduct ordProd = _context.OrderedProduct.Where(ordProd => ordProd.ProductId == orderedProduct.Id).FirstOrDefault();

                    if (ordProd != null)
                    {
                        orderedProductId = _context.OrderedProduct.Where(ordProd => ordProd.ProductId == orderedProduct.Id).FirstOrDefault().Id;
                    }

                    if (orderedProductId == 0)
                    {
                        // TODO: Check values, sanitize.
                        ordProd = new OrderedProduct()
                        {
                            Name = product.Name,
                            Price = product.Price,
                            Sku = product.Sku,
                            Product = product
                        };
                        await _context.OrderedProduct.AddAsync(ordProd);

                        orderedProduct = ordProd;
                    }
                    #endregion

                    #region Add ordered product to OrderedProductTable
                    // Add orderedProduct to OrderedProductOrder-Table if no entry exists.
                    // BUG: id is always 0
                    var ordProdOrd = _context.OrderedProductOrder.Find(ordProd.Id, newOrder.Id);
                    if (ordProdOrd == null || ordProdOrd.Quantity != orderedProductRessource.Quantity)
                    {
                        OrderedProductOrder opo = new OrderedProductOrder()
                        {
                            OrderedProduct = ordProd,
                            Order = newOrder,
                            Quantity = orderedProductRessource.Quantity
                        };
                        await _context.OrderedProductOrder.AddAsync(opo);
                    }
                    #endregion

                    #region Reduce Stock
                    // Reduce the stock of the item on the location with the highest quantity.
                    var productsAtLocation = _context.LocationProduct.OrderByDescending(x => x.Quantity).Where(x => x.ProductId == product.Id).ToList();
                    if (productsAtLocation == null)
                    {
                        return BadRequest($"Not enough quantity for productId: {product.Id}");
                    }

                    // Check if enough products in stock and reduce.
                    uint reduced = ReduceStock(orderedProductRessource, productsAtLocation);

                    // still items to be reduced, but nothing in stock.
                    if (reduced > 0)
                    {
                        return BadRequest($"Not enough quantity for productId: {product.Id}");
                    }
                    #endregion
                }

                var result = await TryContextSaveAsync();

                if (result != null) return result;

                await transaction.CommitAsync();
                return CreatedAtAction(nameof(GetOrderAsync), new { id = newOrder.Id }, newOrder);
            }
        }

        /// <summary>
        /// Reduces the stock of an item.
        /// </summary>
        /// <param name="orderedProductRessource">The product which stock needs to be reduced</param>
        /// <param name="productsAtLocation">List of Locations where the product is in stock</param>
        /// <returns>0 if the stock is reduced successfully, if >0 not enough items in stock</returns>
        private uint ReduceStock(OrderedProductRessource orderedProductRessource, List<LocationProduct> productsAtLocation, uint delta = 0)
        {
            uint reduced;
            if (delta == 0)
                reduced = orderedProductRessource.Quantity;
            else
                reduced = delta;

            foreach (var productAtLocation in productsAtLocation)
            {
                if (reduced > 0)
                {
                    if (productAtLocation.Quantity >= reduced)
                    {
                        productAtLocation.Quantity -= reduced;
                        reduced = 0;
                    }
                    else
                    {
                        reduced -= productAtLocation.Quantity;
                        productAtLocation.Quantity = 0;
                    }
                }
            }

            return reduced;
        }

        /// <summary>
        /// Checks if a order exists in the database.
        /// </summary>
        /// <param name="id">The order-id</param>
        /// <returns>true if order is found, fals if not.</returns>
        private bool OrderExists(uint id)
        {
            return _context.Order.Any(e => e.Id == id);
        }

        /// <summary>
        /// Iterates through all ordered product and sums up the quantity for each.
        /// </summary>
        /// <param name="orderRessourceList">A list of Orders</param>
        /// <returns>Awaitable Task</returns>
        private async Task AddQuantityToOrder(IEnumerable<OrderRessource> orderRessourceList)
        {
            // get orderedProduct quantity
            foreach (var orderRessource in orderRessourceList)
            {
                foreach (var orderedProduct in orderRessource.OrderedProducts)
                {
                    var ordProdOrd = await _context.OrderedProductOrder.FindAsync(orderedProduct.Id, orderRessource.Id);
                    if (ordProdOrd != null)
                        orderedProduct.Quantity = ordProdOrd.Quantity;
                }
            }
        }

        /// <summary>
        /// Reduces the stock of an item by the given quantity.
        /// </summary>
        /// <param name="quantity">The amount to reduce the stock.</param>
        /// <param name="locationId">The id of the location.</param>
        /// <param name="productId">The id of the product.</param>
        /// <returns>true if successful, and false with an error message if not. </returns>
        private async Task<(bool, string)> ReduceStockAsync(uint quantity, uint locationId, uint productId)
        {
            var locationProduct = await _context.LocationProduct.FindAsync(locationId, productId);

            if (locationProduct.Quantity >= quantity)
            {
                locationProduct.Quantity -= quantity;
                return (true, "");
            }
            else
                return (false, "Not enough items in stock.");
        }

        /// <summary>
        /// Saves changes on the context back to the Database
        /// </summary>
        /// <returns>Task or Problem if exception occurs</returns>
        private async Task<ActionResult> TryContextSaveAsync()
        {
            try
            {
                await _context.SaveChangesAsync();
            }
            catch (DbUpdateConcurrencyException e)
            {
                _logger.LogError("DbUpdateConcurrencyException", e);
                return Problem("Could not save to Database", statusCode: 500, title: "Persistence Error");
            }
            catch (Exception exp)
            {
                _logger.LogError("Exception", exp);
                return Problem("Could not save to Database", statusCode: 500, title: "Error");
            }

            return null;
        }
    }
}
